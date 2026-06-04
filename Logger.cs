using System;
using XUnity.Common.Logging;

internal static class Logger
{
  static bool _infoEnabled  = true;
  static bool _warnEnabled  = true;
  static bool _debugEnabled = false;
  // Error 始终启用，不需要标志位

  public static bool IsInfoEnabled  => _infoEnabled;
  public static bool IsWarnEnabled  => _warnEnabled;
  public static bool IsDebugEnabled => _debugEnabled;

  public static void Init(AutoLLMConfig config)
  {
    _debugEnabled = config.DebugEnabled;
    _infoEnabled = config.InfoEnabled;
    _warnEnabled = config.WarnEnabled;
  }

  static void Log(string message, string levelTag)
  {
    var logMessage = $"[ALLM_{levelTag}]: [{DateTime.Now:HH:mm:ss}] {message}";

    if (levelTag == "E")
      XuaLogger.Common.Error(logMessage);
    else if (levelTag == "W")
      XuaLogger.Common.Warn(logMessage);
    else if (levelTag == "D")
      XuaLogger.Common.Debug(logMessage);
    else
      XuaLogger.Common.Info(logMessage);
  }

  public static void Info(string message)  { if (_infoEnabled)  Log(message, "I"); }
  public static void Debug(string message) { if (_debugEnabled) Log(message, "D"); }
  public static void Warn(string message)  { if (_warnEnabled)  Log(message, "W"); }
  public static void Error(string message) => Log(message, "E");
}
