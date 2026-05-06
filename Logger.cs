using System;
using XUnity.Common.Logging;

public static class Logger
{
  public enum LogLevel
  {
    Null,
    Info,
    Error,
    Warning,
    Debug,
  }
  static LogLevel _logLevel = LogLevel.Error;

  public static void InitLogger(LogLevel logLevel = LogLevel.Error)
  {
    _logLevel = logLevel;
  }

  static void Log(string message, LogLevel level)
  {
    if (level > _logLevel) return;

    var logMessage = $"[ALLM_{level.ToString()[0]}]: [{DateTime.Now:HH:mm:ss}] {message}";

    if (level == LogLevel.Error)
      XuaLogger.Common.Error(logMessage);
    else if (level == LogLevel.Warning)
      XuaLogger.Common.Warn(logMessage);
    else if (level == LogLevel.Debug)
      XuaLogger.Common.Debug(logMessage);
    else
      XuaLogger.Common.Info(logMessage);
  }

  public static void Info(string message)
  {
    Log(message, LogLevel.Info);
  }
  public static void Debug(string message)
  {
    Log(message, LogLevel.Debug);
  }
  public static void Warn(string message)
  {
    Log(message, LogLevel.Warning);
  }
  public static void Error(string message)
  {
    Log(message, LogLevel.Error);
  }

}
