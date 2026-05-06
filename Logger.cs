using System;
using System.Collections;
using System.Reflection;
using XUnity.Common.Logging;

public static class Logger
{
  static bool _infoEnabled  = false;
  static bool _warnEnabled  = false;
  static bool _debugEnabled = false;
  // Error 始终启用，不需要标志位

  // 反射检测 BepInEx 框架开启了哪些日志等级，未开启的方法直接 return 减少开销
  public static void AutoDetectLevels()
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

        int combinedFilter = 0;
        foreach (var listener in listeners)
        {
          var filterProp = listener.GetType().GetProperty("LogLevelFilter");
          if (filterProp != null)
            combinedFilter |= (int)filterProp.GetValue(listener, null);
        }

        // BepInEx.Logging.LogLevel: Info=16, Warning=4, Debug=32
        _infoEnabled  = (combinedFilter & 16) != 0;
        _warnEnabled  = (combinedFilter & 4)  != 0;
        _debugEnabled = (combinedFilter & 32) != 0;
        return;
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

  public static void Info(string message)  { if (_infoEnabled)  Log(message, "I"); }
  public static void Debug(string message) { if (_debugEnabled) Log(message, "D"); }
  public static void Warn(string message)  { if (_warnEnabled)  Log(message, "W"); }
  public static void Error(string message) => Log(message, "E");
}
