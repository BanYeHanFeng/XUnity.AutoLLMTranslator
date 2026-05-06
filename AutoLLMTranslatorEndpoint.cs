using System;
using XUnity.AutoTranslator.Plugin.Core.Endpoints;
using XUnity.AutoTranslator.Plugin.Core.Endpoints.Www;

internal class LLMTranslatorEndpoint : WwwEndpoint
{

    #region Since all batching and concurrency are handled within TranslatorTask, please do not modify these two parameters.
    public override int MaxTranslationsPerRequest => 1;
    public override int MaxConcurrency => 500;

    #endregion

    public override string Id => "AutoLLMTranslate";

    public override string FriendlyName => "AutoLLM Translate";
    TranslatorTask task = new TranslatorTask();

    public override void Initialize(IInitializationContext context)
    {
        context.SetTranslationDelay(0.1f);
        task.Init(context);
        Logger.Info("端点初始化完成");
    }

    public override void OnCreateRequest(IWwwRequestCreationContext context)
    {
        if (context.UntranslatedTexts == null || context.UntranslatedTexts.Length == 0)
        {
            Logger.Debug("翻译请求: 空文本，跳过");
            return;
        }
        Logger.Debug($"翻译请求: {context.UntranslatedTexts[0]}");
        context.Complete(new WwwRequestInfo("http://127.0.0.1:20000/", SimpleJson.SerializeTexts(context.UntranslatedTexts)));
    }

    public override void OnExtractTranslation(IWwwTranslationExtractionContext context)
    {
        var data = context.ResponseData;

        Logger.Debug($"翻译结果: {data}");
        var rs = SimpleJson.ParseTexts(data);
        if ((rs?.Length ?? 0) == 0)
        {
            Logger.Error($"翻译结果解析为空: {data}");
            context.Fail("翻译结果为空");
        }
        else
            context.Complete(rs);
    }

}