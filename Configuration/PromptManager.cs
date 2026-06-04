using System;
using System.IO;
using System.Text;


internal static class PromptManager
{
    /// <summary>
    /// 返回已替换 {{SOURCE_LAN}} 和 {{TARGET_LAN}} 的系统提示词。
    /// config.CachedSystemPrompt 应存储此返回值。
    /// </summary>
    public static string Build(AutoLLMConfig config)
    {
        string basePrompt;
        if (!config.CustomPrompt)
        {
            basePrompt = Prompt.Default;
        }
        else
        {
            var path = Path.Combine(config.BepInExRoot, "config", "AutoLLM_CustomPrompt.txt");
            if (File.Exists(path))
            {
                try { basePrompt = File.ReadAllText(path, Encoding.UTF8); }
                catch (Exception ex)
                {
                    Logger.Error("读取自定义提示词失败: " + ex);
                    basePrompt = Prompt.Default;
                }
            }
            else
            {
                try
                {
                    Directory.CreateDirectory(Path.Combine(config.BepInExRoot, "config"));
                    File.WriteAllText(path, Prompt.Default, Encoding.UTF8);
                    Logger.Info("已创建默认自定义提示词: " + path);
                    basePrompt = Prompt.Default;
                }
                catch (Exception ex)
                {
                    Logger.Error("创建自定义提示词失败: " + ex);
                    basePrompt = Prompt.Default;
                }
            }
        }
        return basePrompt
            .Replace("{{SOURCE_LAN}}", config.SourceLanguage)
            .Replace("{{TARGET_LAN}}", config.DestinationLanguage);
    }
}
