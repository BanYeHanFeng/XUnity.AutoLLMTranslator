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
    /// 输入/输出均为数组（按位置对应），不再使用跨批次易混淆的数字编号 key。
    /// </summary>
    /// <param name="result">LLM 调用结果（FullResponse 为完整 JSON）</param>
    /// <param name="batch">本轮任务（顺序对应 JSON 数组中的元素）</param>
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

        // 译文列表：术语表模式取 translations 字段（数组）；普通模式顶层即数组
        List<object> translationsList;
        if (config.AutoGlossary)
        {
            var resultObj = SimpleJson.ParseJsonObject(result.FullResponse);
            if (resultObj == null || resultObj.Count == 0)
                throw new Exception("JSON结果解析失败: " + result.FullResponse);

            if (resultObj.TryGetValue("translations", out object? tObj)
                && tObj is List<object> tList)
                translationsList = tList;
            else
                throw new Exception("结果缺少 translations 数组: " + result.FullResponse);

            if (resultObj.TryGetValue("glossary", out object? gObj)
                && gObj is Dictionary<string, object> gDict)
                glossaryObj = gDict;
        }
        else
        {
            // 普通模式：结果顶层为 ["译文1","译文2",...]
            translationsList = SimpleJson.ParseJsonArray(result.FullResponse);
            if (translationsList.Count == 0)
                throw new Exception("JSON数组解析失败: " + result.FullResponse);
        }

        // 数组按位置与 batch 一一对应（无编号，杜绝跨批次同号混淆）
        int completed = 0;
        for (int i = 0; i < translationsList.Count && i < batch.Count; i++)
        {
            string translated = (translationsList[i] as string) ?? "";
            if (string.IsNullOrEmpty(translated)) continue;

            // 全角转半角
            if (config.HalfWidth)
                translated = HalfWidthRegex.Replace(translated,
                    m => ((char)(m.Value[0] - 0xFEE0)).ToString());

            batch[i].MarkCompleted(translated);
            completed++;
        }

        return completed;
    }
}