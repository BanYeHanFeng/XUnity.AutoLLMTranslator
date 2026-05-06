using System;
using XUnity.Common.Logging;

public static class Logger
{
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

  public static void Info(string message)  => Log(message, "I");
  public static void Debug(string message) => Log(message, "D");
  public static void Warn(string message)  => Log(message, "W");
  public static void Error(string message) => Log(message, "E");
}
