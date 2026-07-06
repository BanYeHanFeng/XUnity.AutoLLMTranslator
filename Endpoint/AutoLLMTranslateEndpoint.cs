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
        try
        {
            context.SetTranslationDelay(0.1f);

            var config = AutoLLMConfig.FromInitializationContext(context);
            if (!config.IsValid)
            {
                Logger.Error("Model 或 URL 未配置，翻译功能已禁用");
                return;
            }

            Logger.Init(config);
            Logger.Info("端点初始化完成");

            _orchestrator = new TranslationOrchestrator(config, new LlmClient());
            _orchestrator.Start();
            _initialized = true;

            Logger.Info("翻译服务已启动 | 模型=" + config.Model + " 地址=" + config.Url +
                " 最大上下文=" + config.MaxContext);
        }
        catch (Exception ex)
        {
            Logger.Error("端点初始化异常", ex);
        }
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
            Logger.Debug("翻译请求: 空文本，跳过");
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
