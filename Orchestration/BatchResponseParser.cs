using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;


/// <summary>
/// 解析 LLM 返回的 JSON 响应并分发到批内任务：JSON → 译文映射 → 按需全角转半角 → MarkCompleted。
/// 术语表模式额外输出本轮新术语（glossaryObj）供 Orchestrator 收集。
/// 纯转换逻辑，不接触队列、历史、重试或限速状态。
/// </summary>
internal static class BatchResponseParser
{
    // HalfWidthRegex: 全角符号 [！-～] (U+FF01 - U+FF5E)
    // 用 Unicode 范围替代显式字符列表，避免 verbatim string 中 "" 转义带来的字符类构成错误
    // （旧实现误包含半角 " U+0022 而漏掉全角 ＂ U+FF02，导致半角双引号被错误映射到 U+0142）
    private static readonly Regex HalfWidthRegex =
        new Regex(@"[\uFF01-\uFF5E]", RegexOptions.Compiled);

    /// <summary>
    /// 解析 LLM 响应、按需半角化译文、为批内任务标记完成。
    /// </summary>
    /// <param name="result">LLM 调用结果（FullResponse 为完整 JSON）</param>
    /// <param name="batch">本轮任务（顺序对应 JSON 中的 "1".."N"）</param>
    /// <param name="config">配置（用于 AutoGlossary 模式判定与 HalfWidth）</param>
    /// <param name="glossaryObj">术语表模式下，返回本轮新术语映射；否则为 null</param>
    /// <returns>成功标记完成的任务数；抛异常表示解析失败或结果为空</returns>
    public static int ParseAndDispatch(
        LlmResult result,
        List<TranslationTask> batch,
        AutoLLMConfig config,
        out Dictionary<string, object>? glossaryObj)
    {
        glossaryObj = null;

        if (string.IsNullOrEmpty(result.FullResponse))
            throw new Exception("翻译结果为空");

        var resultObj = SimpleJson.ParseJsonObject(result.FullResponse);
        if (resultObj == null || resultObj.Count == 0)
            throw new Exception("JSON结果解析失败: " + result.FullResponse);

        // 分发结果：术语表模式用 translations 嵌套结构，否则用扁平数字 key
        Dictionary<string, object>? translationsObj;
        if (config.AutoGlossary && resultObj.TryGetValue("translations", out object? tObj)
            && tObj is Dictionary<string, object> tDict)
        {
            translationsObj = tDict;
            if (resultObj.TryGetValue("glossary", out object? gObj)
                && gObj is Dictionary<string, object> gDict)
                glossaryObj = gDict;
        }
        else
        {
            // 非术语表模式：整个对象就是译文映射
            translationsObj = resultObj;
        }

        int completed = 0;
        foreach (var kvp in translationsObj)
        {
            int index;
            if (!int.TryParse(kvp.Key, out index)) continue;
            if (index < 1 || index > batch.Count) continue;

            string translated = (kvp.Value as string) ?? "";
            if (string.IsNullOrEmpty(translated)) continue;

            // 全角转半角
            if (config.HalfWidth)
                translated = HalfWidthRegex.Replace(translated,
                    m => ((char)(m.Value[0] - 0xFEE0)).ToString());

            batch[index - 1].MarkCompleted(translated);
            completed++;
        }

        return completed;
    }
}