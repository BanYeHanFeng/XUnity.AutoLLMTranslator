using System;
using XUnity.Common.Logging;

// 日志底层与框架自带的翻译器（GoogleTranslate / DeepL / Bing …）保持一致：
// 统一经 XuaLogger.AutoTranslator 转发，由 BepInEx 的 Console/Disk listener 按
// BepInEx.cfg 的 [Logging.Console]/[Logging.Disk].LogLevels 过滤。
// 本类只做转发 + [AutoLLM] 标签统一加注，不再本地重复门控日志级别。
internal static class Logger
{
    private const string Tag = "[AutoLLM] ";

    public static void Info(string message)
    {
        XuaLogger.AutoTranslator.Info(Tag + message);
    }

    public static void Info(string message, Exception? ex)
    {
        XuaLogger.AutoTranslator.Info(ex, Tag + message);
    }

    public static void Debug(string message)
    {
        XuaLogger.AutoTranslator.Debug(Tag + message);
    }

    public static void Debug(string message, Exception? ex)
    {
        XuaLogger.AutoTranslator.Debug(ex, Tag + message);
    }

    public static void Warn(string message)
    {
        XuaLogger.AutoTranslator.Warn(Tag + message);
    }

    public static void Warn(string message, Exception? ex)
    {
        XuaLogger.AutoTranslator.Warn(ex, Tag + message);
    }

    public static void Error(string message)
    {
        try { XuaLogger.AutoTranslator.Error(Tag + message); }
        catch { Console.Error.WriteLine("[ALLM_Error]: " + message); }
    }

    public static void Error(string message, Exception? ex)
    {
        try { XuaLogger.AutoTranslator.Error(ex, Tag + message); }
        catch { Console.Error.WriteLine("[ALLM_Error]: " + message + " | " + ex); }
    }
}