using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;


internal class LlmClient : ILlmClient
{
    // 单例标志（static：跨批次共享）
    private static bool _warnedUsageMissing = false;
    private static bool _cacheStatsSupported = false;
    private static bool _cacheStatsChecked = false;

    public static bool CacheStatsSupported => _cacheStatsSupported;

    public LlmResult Translate(
        string url, string apiKey, string model,
        List<LlmMessage> messages,
        Dictionary<string, object> extraParams)
    {
        // 1. 构建请求体（与原始 LlmClient.Translate 完全一致）
        var requestBody = new Dictionary<string, object>();
        foreach (var kv in extraParams)
            requestBody[kv.Key] = kv.Value;
        requestBody["model"] = model;
        requestBody["messages"] = SerializeMessages(messages);
        requestBody["response_format"] = new Dictionary<string, object> { { "type", "json_object" } };
        requestBody["stream"] = true;
        requestBody["stream_options"] = new Dictionary<string, object> { { "include_usage", true } };
        string requestJson = SimpleJson.Serialize(requestBody);

        // 2. 发送 HttpWebRequest（Timeout=600000, ReadWriteTimeout=120000）
        var httpRequest = (HttpWebRequest)WebRequest.Create(url);
        httpRequest.Method = "POST";
        httpRequest.Timeout = 600000;
        httpRequest.ReadWriteTimeout = 120000;
        if (!string.IsNullOrEmpty(apiKey))
            httpRequest.Headers.Add("Authorization", "Bearer " + apiKey);
        httpRequest.ContentType = "application/json";

        using (var sw = new StreamWriter(httpRequest.GetRequestStream()))
            sw.Write(requestJson);

        long startTick = Environment.TickCount;

        // 3. 读取 SSE 流（与原始代码完全一致的逐行解析逻辑）
        using (var response = (HttpWebResponse)httpRequest.GetResponse())
        using (var stream = response.GetResponseStream())
        using (var reader = new StreamReader(stream))
        {
            var fullResponse = new StringBuilder();
            var usage = new Dictionary<string, object>();
            int chunks = 0;
            bool done = false;
            string line;

            while ((line = reader.ReadLine()) != null)
            {
                if (string.IsNullOrEmpty(line)) continue;
                if (!line.StartsWith("data: ")) continue;
                string data = line.Substring(6);
                if (data == "[DONE]") { done = true; break; }
                chunks++;
                SimpleJson.ParseSseChunk(data, out string content, out Dictionary<string, object> u);
                if (!string.IsNullOrEmpty(content))
                    fullResponse.Append(content);
                if (u != null) usage = u;
            }

            if (!done && fullResponse.Length > 0)
                Logger.Debug("流式响应未收到[DONE]标记 (数据块=" + chunks + ")");

            // 4. 构建结果
            var result = new LlmResult
            {
                FullResponse = fullResponse.ToString(),
                ChunkCount = chunks,
                DoneReceived = done,
                ElapsedMs = Environment.TickCount - startTick
            };

            // 5. 提取 token 用量
            ExtractUsage(usage, result);

            return result;
        }
    }

    /// <summary>将 LlmMessage 列表转为 SimpleJson 可序列化的 List&lt;Dictionary&gt;。</summary>
    private static List<Dictionary<string, object>> SerializeMessages(List<LlmMessage> messages)
    {
        var list = new List<Dictionary<string, object>>();
        foreach (var msg in messages)
        {
            list.Add(new Dictionary<string, object>
            {
                { "role", msg.Role },
                { "content", msg.Content }
            });
        }
        return list;
    }

    /// <summary>从 usage dict 提取 token 统计（与原始代码逻辑完全一致）。</summary>
    private static void ExtractUsage(Dictionary<string, object> usage, LlmResult result)
    {
        if (usage.ContainsKey("prompt_tokens"))
        {
            result.Usage = new LlmUsage();
            result.Usage.PromptTokens = Convert.ToInt64(usage["prompt_tokens"]);
            result.Usage.CompletionTokens = usage.ContainsKey("completion_tokens")
                ? Convert.ToInt64(usage["completion_tokens"]) : 0;

            if (!_cacheStatsChecked)
            {
                _cacheStatsChecked = true;
                _cacheStatsSupported = usage.ContainsKey("prompt_cache_hit_tokens")
                    || usage.ContainsKey("prompt_cache_miss_tokens");
                if (!_cacheStatsSupported)
                    Logger.Info("接口流式响应不含缓存命中/未中统计");
            }

            if (_cacheStatsSupported)
            {
                if (usage.TryGetValue("prompt_cache_hit_tokens", out object hit))
                    result.Usage.CacheHitTokens = Convert.ToInt64(hit);
                if (usage.TryGetValue("prompt_cache_miss_tokens", out object miss))
                    result.Usage.CacheMissTokens = Convert.ToInt64(miss);
            }
        }
        else if (!_warnedUsageMissing)
        {
            Logger.Info("用量字段缺失，接口可能不支持 token 统计");
            _warnedUsageMissing = true;
        }
    }
}
