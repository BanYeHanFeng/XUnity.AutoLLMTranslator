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
            Logger.Info("使用默认系统提示词 (" + basePrompt.Length + " 字符)");
        }
        else if (string.IsNullOrEmpty(config.BepInExRoot))
        {
            // BepInEx 根目录定位失败时 Path.Combine 会抛 ArgumentNullException，
            // 此处显式回退到默认提示词，避免端点静默禁用
            Logger.Warn("BepInEx 根目录未定位到，自定义提示词不可用，回退默认提示词");
            basePrompt = Prompt.Default;
        }
        else
        {
            var path = Path.Combine(Path.Combine(config.BepInExRoot!, "config"), "AutoLLM_CustomPrompt.txt");
            if (File.Exists(path))
            {
                try
                {
                    basePrompt = File.ReadAllText(path, Encoding.UTF8);
                    Logger.Info("已加载自定义提示词: " + path + " (长度=" + basePrompt.Length + " 字符)");
                }
                catch (Exception ex)
                {
                    Logger.Error("读取自定义提示词失败", ex);
                    basePrompt = Prompt.Default;
                }
            }
            else
            {
                try
                {
                    Directory.CreateDirectory(Path.Combine(config.BepInExRoot!, "config"));
                    File.WriteAllText(path, Prompt.Default, Encoding.UTF8);
                    Logger.Info("已创建默认自定义提示词模板: " + path);
                    basePrompt = Prompt.Default;
                }
                catch (Exception ex)
                {
                    Logger.Error("创建自定义提示词模板失败", ex);
                    basePrompt = Prompt.Default;
                }
            }
        }
        return basePrompt
            .Replace("{{SOURCE_LAN}}", config.SourceLanguage ?? "")
            .Replace("{{TARGET_LAN}}", config.DestinationLanguage ?? "")
            // 统一行尾为 LF：避免 Windows(CRLF)/Linux(LF) 构建产物字符串长度漂移，
            // 同时使 token 估算（chars×0.75）跨平台一致
            .Replace("\r\n", "\n")
            .Replace("\r", "\n");
    }
}
