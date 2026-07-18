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
    /// 输入/输出键值对应：输入 JSON 为 {"1":"原文1","2":"原文2",...}，输出为
    /// {"1":"译文1","2":"译文2",...}（普通模式）；术语表模式再追加 "glossary":{...}。
    /// 各编号由 ConversationHistory.AllocKeys 全局单调分配，同一历史窗口内绝不重号，
    /// 故模型不会把当前输入与历史译文按相同编号合并。
    /// </summary>
    /// <param name="result">LLM 调用结果（FullResponse 为完整 JSON）</param>
    /// <param name="batch">本轮任务（UserKey 已由 AllocKeys 设置）</param>
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

        // 译文：按各任务的 UserKey 从结果对象中查找对应译文（输入/输出键值对应）。
        // 允许 LLM 漏答个别键（视为本条未完成）或乱序输出，键值匹配不依赖数组下标。
        int completed = 0;
        foreach (var task in batch)
        {
            string key = task.UserKey;
            if (string.IsNullOrEmpty(key))
                continue;
            if (!resultObj.TryGetValue(key, out object? vObj))
                continue;

            string? translated = vObj as string;
            if (translated == null)
            {
                // 非字符串值兜底为字符串表示，再交由 HalfWidth/空判处理
                translated = vObj != null ? vObj.ToString() : "";
            }
            if (string.IsNullOrEmpty(translated)) continue;

            // 全角转半角
            if (config.HalfWidth)
                translated = HalfWidthRegex.Replace(translated,
                    m => ((char)(m.Value[0] - 0xFEE0)).ToString());

            task.MarkCompleted(translated);
            completed++;
        }

        // 术语表模式：额外提取 glossary 新术语（与译文键不冲突，单独的 "glossary" 对象）
        if (config.AutoGlossary
            && resultObj.TryGetValue("glossary", out object? gObj)
            && gObj is Dictionary<string, object> gDict)
            glossaryObj = gDict;

        return completed;
    }
}