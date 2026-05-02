using XUnity.AutoTranslator.Plugin.Core.Endpoints;
using System.Collections.Generic;
using System;
public class TranslateDB
{
  Dictionary<int, string> terminology = new Dictionary<int, string>();

  public void Init(IInitializationContext context, string _terminology)
  {
    if (!string.IsNullOrEmpty(_terminology))
    {
      var entries = _terminology.Split('|');
      foreach (var entry in entries)
      {
        var parts = entry.Split(new string[] { "==" }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2)
        {
          var hash = parts[0].GetHashCode();
          if (!terminology.ContainsKey(hash))
          {
            Logger.Debug($"添加词条: {parts[0]} = {parts[1]}");
            terminology.Add(hash, parts[1]);
          }
        }
        else
        {
          Logger.Error($"词条格式错误: {entry}");
        }
      }
    }
  }

  public string? FindTerminology(string text)
  {
    var hash = text.GetHashCode();
    if (terminology.TryGetValue(hash, out string value))
      return value;
    return null;
  }
}
