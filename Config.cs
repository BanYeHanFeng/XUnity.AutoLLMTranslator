using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;


public static class Config
{
    public static string prompt_base = @"将{{SOURCE_LAN}}翻译为{{TARGET_LAN}}，严格按照以下规则：

1. 保留原文格式：%s、[TAG]、<label>、HTML标签等，不添加原文没有的内容
2. 同批文本无关联，每条独立翻译，不可混淆
3. 不添加解释说明，不混入其他语言
4. 每条译文在同一行内，换行用\n表示
5. 输出为JSON对象，键与输入一致

输入格式：{""1"": ""原文1"", ""2"": ""原文2""}
输出格式：{""1"": ""译文1"", ""2"": ""译文2""}";
}
