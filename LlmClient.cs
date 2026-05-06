using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;

internal static class LlmClient
{
    public class Result
    {
        public string FullResponse;
        public long PromptTokens;
        public long CompletionTokens;
        public long CacheHitTokens;
        public long CacheMissTokens;
        public int ChunkCount;
        public bool DoneReceived;
        public long ElapsedMs;
    }

    static bool _warnedUsageMissing = false;
    static bool _cacheStatsSupported = false;
    static bool _cacheStatsChecked = false;

    public static bool CacheStatsSupported { get { return _cacheStatsSupported; } }

    public static Result Translate(string url, string apiKey, string model, List<object> messages, string modelParams)
    {
        var requestBody = new Dictionary<string, object>();

        if (!string.IsNullOrEmpty(modelParams))
        {
            var data = SimpleJson.ParseModelParams(modelParams);
            foreach (var kv in data)
                requestBody[kv.Key] = kv.Value;
        }

        requestBody["model"] = model;
        requestBody["messages"] = messages;
        requestBody["response_format"] = new Dictionary<string, object> { { "type", "json_object" } };
        requestBody["stream"] = true;
        var requestJson = SimpleJson.Serialize(requestBody);

        var httpRequest = (HttpWebRequest)WebRequest.Create(url);
        httpRequest.Method = "POST";
        httpRequest.Timeout = 60000;
        httpRequest.ReadWriteTimeout = 60000;
        if (!string.IsNullOrEmpty(apiKey))
            httpRequest.Headers.Add("Authorization", "Bearer " + apiKey);
        httpRequest.ContentType = "application/json";

        using (var sw = new StreamWriter(httpRequest.GetRequestStream()))
            sw.Write(requestJson);

        var startTick = Environment.TickCount;

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

                var data = line.Substring(6);
                if (data == "[DONE]") { done = true; break; }

                chunks++;
                try
                {
                    var content = SimpleJson.ParseSseContent(data);
                    if (!string.IsNullOrEmpty(content))
                        fullResponse.Append(content);

                    var u = SimpleJson.ParseSseUsage(data);
                    if (u != null) usage = u;
                }
                catch (Exception ex)
                {
                    Logger.Error("解析流响应出错: " + ex.Message);
                }
            }

            if (!done && fullResponse.Length > 0)
                Logger.Warn("SSE 流未收到 [DONE]，响应可能不完整 (chunks=" + chunks + ")");

            var result = new Result
            {
                FullResponse = fullResponse.ToString(),
                ChunkCount = chunks,
                DoneReceived = done,
                ElapsedMs = Environment.TickCount - startTick
            };

            if (usage.ContainsKey("prompt_tokens"))
            {
                result.PromptTokens = Convert.ToInt64(usage["prompt_tokens"]);
                result.CompletionTokens = usage.ContainsKey("completion_tokens") ? Convert.ToInt64(usage["completion_tokens"]) : 0;

                if (!_cacheStatsChecked)
                {
                    _cacheStatsChecked = true;
                    _cacheStatsSupported = usage.ContainsKey("prompt_cache_hit_tokens") || usage.ContainsKey("prompt_cache_miss_tokens");
                    if (!_cacheStatsSupported)
                        Logger.Info("API 流式响应不返回缓存命中/未中统计，缓存统计将始终为 0，但不影响实际缓存效果");
                }

                if (_cacheStatsSupported)
                {
                    if (usage.TryGetValue("prompt_cache_hit_tokens", out object hit)) result.CacheHitTokens = Convert.ToInt64(hit);
                    if (usage.TryGetValue("prompt_cache_miss_tokens", out object miss)) result.CacheMissTokens = Convert.ToInt64(miss);
                }
            }
            else if (!_warnedUsageMissing)
            {
                Logger.Debug("usage 字段未返回，API 可能不支持 token 统计");
                _warnedUsageMissing = true;
            }

            return result;
        }
    }
}
