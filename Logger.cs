using System;
using System.Collections;
using System.Reflection;
using XUnity.Common.Logging;

public static class Logger
{
  static bool _debugEnabled = false;

  // 通过反射检测 BepInEx 框架是否开启了 Debug 日志等级
  public static void AutoDetectDebug()
  {
    try
    {
      foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
      {
        if (!asm.GetName().Name.StartsWith("BepInEx")) continue;
        var loggerType = asm.GetType("BepInEx.Logging.Logger");
        if (loggerType == null) continue;
        var listenersProp = loggerType.GetProperty("Listeners", BindingFlags.Public | BindingFlags.Static);
        if (listenersProp == null) continue;
        var listeners = listenersProp.GetValue(null) as IEnumerable;
        if (listeners == null) continue;
        foreach (var listener in listeners)
        {
          var filterProp = listener.GetType().GetProperty("LogLevelFilter");
          if (filterProp != null)
          {
            int filterVal = (int)filterProp.GetValue(listener, null);
            if ((filterVal & 32) != 0) // BepInEx.Logging.LogLevel.Debug = 32
            {
              _debugEnabled = true;
              return;
            }
          }
        }
      }
    }
    catch { }
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

  public static void Info(string message)  => Log(message, "I");
  public static void Debug(string message) { if (_debugEnabled) Log(message, "D"); }
  public static void Warn(string message)  => Log(message, "W");
  public static void Error(string message) => Log(message, "E");
}
