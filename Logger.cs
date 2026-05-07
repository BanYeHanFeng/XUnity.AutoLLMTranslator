using System;
using System.Collections.Generic;
using System.IO;
using XUnity.Common.Logging;

public static class Logger
{
  static bool _infoEnabled  = true;
  static bool _warnEnabled  = true;
  static bool _debugEnabled = false;
  // Error 始终启用，不需要标志位

  public static bool IsInfoEnabled  => _infoEnabled;
  public static bool IsWarnEnabled  => _warnEnabled;
  public static bool IsDebugEnabled => _debugEnabled;

  // 从 BepInEx/config/BepInEx.cfg 读取日志等级
  public static void Init(string bepinExRoot)
  {
    try
    {
      var cfgPath = Path.Combine(Path.Combine(bepinExRoot, "config"), "BepInEx.cfg");
      if (!File.Exists(cfgPath)) return;

      var sections = ParseIniFile(cfgPath);

      // [Logging.Console]
      bool cEnabled = false, cDebug = false, cInfo = false, cWarn = false;
      if (sections.TryGetValue("Logging.Console", out var cSec))
      {
        cEnabled = GetBoolValue(cSec, "Enabled", false);
        if (cSec.TryGetValue("LogLevels", out var cLevels))
        {
          cDebug = ContainsLevel(cLevels, "Debug");
          cInfo  = ContainsLevel(cLevels, "Info");
          cWarn  = ContainsLevel(cLevels, "Warning");
        }
      }

      // [Logging.Disk] — Disk 默认开启
      bool dEnabled = true, dDebug = false, dInfo = false, dWarn = false;
      if (sections.TryGetValue("Logging.Disk", out var dSec))
      {
        dEnabled = GetBoolValue(dSec, "Enabled", true);
        if (dSec.TryGetValue("LogLevels", out var dLevels))
        {
          dDebug = ContainsLevel(dLevels, "Debug");
          dInfo  = ContainsLevel(dLevels, "Info");
          dWarn  = ContainsLevel(dLevels, "Warning");
        }
      }

      // 综合：任一端开启即生效
      _debugEnabled = (cEnabled && cDebug) || (dEnabled && dDebug);
      _infoEnabled  = (cEnabled && cInfo)  || (dEnabled && dInfo);
      _warnEnabled  = (cEnabled && cWarn)  || (dEnabled && dWarn);
    }
    catch { }
  }

  // 简易 INI 解析器
  static Dictionary<string, Dictionary<string, string>> ParseIniFile(string path)
  {
    var result = new Dictionary<string, Dictionary<string, string>>();
    string? currentSection = null;
    foreach (var line in File.ReadAllLines(path))
    {
      var trimmed = line.Trim();
      if (string.IsNullOrEmpty(trimmed) || trimmed[0] == '#' || trimmed[0] == ';')
        continue;
      if (trimmed[0] == '[' && trimmed[trimmed.Length - 1] == ']')
      {
        currentSection = trimmed.Substring(1, trimmed.Length - 2);
        if (!result.ContainsKey(currentSection))
          result[currentSection] = new Dictionary<string, string>();
        continue;
      }
      if (currentSection != null)
      {
        int eq = trimmed.IndexOf('=');
        if (eq > 0)
        {
          var key = trimmed.Substring(0, eq).Trim();
          var val = trimmed.Substring(eq + 1).Trim();
          result[currentSection][key] = val;
        }
      }
    }
    return result;
  }

  // 检查逗号分隔列表中是否包含指定等级（含 All）
  static bool ContainsLevel(string levels, string levelName)
  {
    if (string.IsNullOrEmpty(levels)) return false;
    foreach (var part in levels.Split(','))
    {
      var t = part.Trim();
      if (t.Equals("All", StringComparison.OrdinalIgnoreCase)) return true;
      if (t.Equals(levelName, StringComparison.OrdinalIgnoreCase)) return true;
    }
    return false;
  }

  static bool GetBoolValue(Dictionary<string, string> section, string key, bool defaultValue)
  {
    if (section.TryGetValue(key, out var value))
    {
      if (bool.TryParse(value, out var result)) return result;
      if (value == "1") return true;
      if (value == "0") return false;
    }
    return defaultValue;
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
