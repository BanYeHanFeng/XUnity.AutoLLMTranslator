using System;
using System.Collections;
using XUnity.AutoTranslator.Plugin.Core.Endpoints;


internal class AutoLLMTranslateEndpoint : ITranslateEndpoint, IDisposable
{
    private TranslationOrchestrator? _orchestrator;
    private bool _initialized;

    // ---- ITranslateEndpoint 成员 ----

    public string Id => "AutoLLMTranslate";
    public string FriendlyName => "AutoLLM Translate";

    // 框架调度参数：由内部队列管理，框架不做批量/并发限制
    public int MaxTranslationsPerRequest => 1;
    public int MaxConcurrency => 500;

    public void Initialize(IInitializationContext context)
    {
        context.SetTranslationDelay(0.1f);

        // FromInitializationContext 在必填项缺失时抛 EndpointInitializationException，
        // 交由框架的 TranslationManager 统一捕获并标记端点初始化失败（与其它端点一致）。
        var config = AutoLLMConfig.FromInitializationContext(context);

        Logger.Info("端点初始化完成");

        _orchestrator = new TranslationOrchestrator(config, new LlmClient());
        _orchestrator.Start();
        _initialized = true;

        Logger.Info("翻译服务已启动 | 模型=" + config.Model + " 地址=" + config.Url +
            " 端点=" + config.EndpointUrl + " 最大上下文=" + config.MaxContext);
    }

    public IEnumerator Translate(ITranslationContext context)
    {
        if (!_initialized || _orchestrator == null)
        {
            context.Fail("端点未初始化");
            yield break;
        }

        if (string.IsNullOrEmpty(context.UntranslatedText))
        {
            // 显式 Fail：原先仅 yield break 不调用 Complete/Fail，
            // 依赖框架 finally 的 FailIfNotCompleted() 兜底，错误信息为
            // 通用文案"The translation request was not completed before returning from translator."，
            // 排查不直观。此处改为显式 Fail 以给出明确语义（与上方“端点未初始化”路径风格一致）。
            Logger.Debug("翻译请求: 空文本，跳过");
            context.Fail("空文本，无翻译内容");
            yield break;
        }

        var task = new TranslationTask
        {
            UntranslatedText = context.UntranslatedText!,
            CharLen = context.UntranslatedText!.Length,
            CreatedTick = Environment.TickCount,
        };

        if (!_orchestrator.Queue.TryEnqueue(task))
        {
            context.Fail("任务队列已满");
            yield break;
        }

        // 协程轮询，等待任务完成
        while (!task.IsCompleted)
            yield return null;   // Unity 主线程每帧检查

        if (task.State == TaskState.Completed)
            context.Complete(task.TranslatedText ?? "");
        else
            context.Fail(task.ErrorMessage ?? "翻译失败");
    }

    // ---- 清理 ----
    public void Dispose()
    {
        _orchestrator?.Shutdown();
    }
}
