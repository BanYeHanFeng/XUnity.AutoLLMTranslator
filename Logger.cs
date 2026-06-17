#nullable disable
using System;
using XUnity.Common.Logging;

internal static class Logger
{
    static bool _infoEnabled = true;
    static bool _warnEnabled = true;
    static bool _debugEnabled = false;
    // 错误级别始终启用，不需要标志位

    public static bool IsInfoEnabled  => _infoEnabled;
    public static bool IsWarnEnabled  => _warnEnabled;
    public static bool IsDebugEnabled => _debugEnabled;

    public static void Init(AutoLLMConfig config)
    {
        _debugEnabled = config.DebugEnabled;
        _infoEnabled = config.InfoEnabled;
        _warnEnabled = config.WarnEnabled;
    }

    public static void Info(string message)
    {
        if (_infoEnabled) XuaLogger.Common.Info(message);
    }

    public static void Info(string message, System.Exception ex)
    {
        if (_infoEnabled) XuaLogger.Common.Info(ex, message);
    }

    public static void Debug(string message)
    {
        if (_debugEnabled) XuaLogger.Common.Debug(message);
    }

    public static void Debug(string message, System.Exception ex)
    {
        if (_debugEnabled) XuaLogger.Common.Debug(ex, message);
    }

    public static void Warn(string message)
    {
        if (_warnEnabled) XuaLogger.Common.Warn(message);
    }

    public static void Warn(string message, System.Exception ex)
    {
        if (_warnEnabled) XuaLogger.Common.Warn(ex, message);
    }

    public static void Error(string message)
    {
        try { XuaLogger.Common.Error(message); }
        catch { Console.Error.WriteLine("[ALLM_Error]: " + message); }
    }

    public static void Error(string message, System.Exception ex)
    {
        try { XuaLogger.Common.Error(ex, message); }
        catch { Console.Error.WriteLine("[ALLM_Error]: " + message + " | " + ex); }
    }
}
