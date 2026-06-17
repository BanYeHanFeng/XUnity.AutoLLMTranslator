using System;
using System.Collections.Generic;


internal interface ILlmClient
{
    /// <summary>
    /// 同步发送翻译请求到 LLM API，返回结果或抛出异常。
    /// 调用线程会被阻塞（HttpWebRequest 是同步的），由调用方负责在后台线程上调用。
    /// </summary>
    /// <throws>WebException（网络/HTTP 错误，含 429）</throws>
    /// <throws>Exception（解析失败等其他错误）</throws>
    LlmResult Translate(
        string url,
        string apiKey,
        string model,
        List<LlmMessage> messages,
        Dictionary<string, object> extraParams);
}
