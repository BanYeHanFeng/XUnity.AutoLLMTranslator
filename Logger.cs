using System;
using System.IO;
using XUnity.Common.Logging;

public static class Logger
{
  static bool _infoEnabled  = true;
  static bool _warnEnabled  = true;
  static bool _debugEnabled = false;
  // Error 始终启用，不需要标志位

  static string? _logFilePath = null;
  static readonly object _fileLock = new object();

  public static bool IsInfoEnabled  => _infoEnabled;
  public static bool IsWarnEnabled  => _warnEnabled;
  public static bool IsDebugEnabled => _debugEnabled;

  // LogLevel: Error / Warning / Info / Debug，默认 Info
  public static void SetLevel(string level)
  {
    // 默认: Info (Error+Warn+Info, 关闭 Debug)
    _infoEnabled  = true;
    _warnEnabled  = true;
    _debugEnabled = false;

    var l = (level ?? "").Trim();
    if (l.Equals("Error", StringComparison.OrdinalIgnoreCase))
    {
      _infoEnabled  = false;
      _warnEnabled  = false;
    }
    else if (l.Equals("Warning", StringComparison.OrdinalIgnoreCase))
    {
      _infoEnabled  = false;
    }
    else if (l.Equals("Debug", StringComparison.OrdinalIgnoreCase))
    {
      _debugEnabled = true;
    }
    // "Info" 或空: 保持默认值不变
  }

  // Log2File: 额外写入 AutoLLM.log
  public static void SetLogFile(string? path)
  {
    _logFilePath = path;
  }

  static void WriteToFile(string message)
  {
    if (string.IsNullOrEmpty(_logFilePath)) return;
    lock (_fileLock)
    {
      try
      {
        var dir = Path.GetDirectoryName(_logFilePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
          Directory.CreateDirectory(dir);
        File.AppendAllText(_logFilePath, message + Environment.NewLine);
      }
      catch { }
    }
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

    WriteToFile(logMessage);
  }

  public static void Info(string message)  { if (_infoEnabled)  Log(message, "I"); }
  public static void Debug(string message) { if (_debugEnabled) Log(message, "D"); }
  public static void Warn(string message)  { if (_warnEnabled)  Log(message, "W"); }
  public static void Error(string message) => Log(message, "E");
}
