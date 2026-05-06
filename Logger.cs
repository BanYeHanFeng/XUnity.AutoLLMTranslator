using System;
using XUnity.Common.Logging;

public static class Logger
{
  static System.IO.StreamWriter _logger = null;
  public enum LogLevel
  {
    Null,
    Info,
    Error,
    Warning,
    Debug,
  }
  static LogLevel _logLevel = LogLevel.Error;
  static bool _log2file = false;

  public static void InitLogger(LogLevel logLevel = LogLevel.Error, bool log2file = false)
  {
    _logLevel = logLevel;
    _log2file = log2file;
    if (_logLevel == LogLevel.Null) return;
    if (_log2file && _logger == null)
    {
      string appDirectory = AppDomain.CurrentDomain.BaseDirectory;
      // 日志写入游戏根目录/BepInEx/，从 Managed/ 向上查找
      string gameRoot = System.IO.Path.GetFullPath(System.IO.Path.Combine(appDirectory, ".."));
      for (int i = 0; i < 2; i++)
      {
          if (System.IO.Directory.Exists(System.IO.Path.Combine(gameRoot, "BepInEx")))
              break;
          gameRoot = System.IO.Path.GetFullPath(System.IO.Path.Combine(gameRoot, ".."));
      }
      var logDir = System.IO.Path.Combine(gameRoot, "BepInEx");
      System.IO.Directory.CreateDirectory(logDir);
      var logfile = System.IO.Path.Combine(logDir, "AutoLLM.log");
      _logger = new System.IO.StreamWriter(logfile, true);
      _logger.AutoFlush = true;
    }
  }

  private static readonly object _logLock = new object();

  static void Log(string message, LogLevel level)
  {
    bool logToXua = level <= _logLevel;
    bool logToFile = _logger != null;
    if (!logToXua && !logToFile) return;

    var logMessage = $"[ALLM_{level.ToString()[0]}]: [{DateTime.Now:HH:mm:ss}] {message}";
    
    lock (_logLock)
    {
      if (logToXua)
      {
        if (level == LogLevel.Error)
          XuaLogger.Common.Error(logMessage);
        else if (level == LogLevel.Warning)
          XuaLogger.Common.Warn(logMessage);
        else if (level == LogLevel.Debug)
          XuaLogger.Common.Debug(logMessage);
        else
          XuaLogger.Common.Info(logMessage);
      }
      if (logToFile)
        _logger.WriteLine(logMessage);
    }
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