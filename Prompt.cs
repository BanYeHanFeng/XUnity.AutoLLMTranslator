using System;


internal static class Prompt
{
    public const string Default = @"我是游戏翻译家，接下来从{{SOURCE_LAN}}翻译为{{TARGET_LAN}}，规则：
1.保留所有占位符(如：/n，%s)
2.不添加解释性说明
3.给出符合{{TARGET_LAN}}的翻译
4.输出合法JSON
示例：
输入：{""1"":""Hello"", ""2"":""World""}
输出：{""1"":""你好"", ""2"":""世界""}";

    /// <summary>
    /// 自动术语表模式系统提示词。
    /// 要求模型在翻译同时输出新术语，输出结构为 {"translations":{...},"glossary":{...}}。
    /// 术语表占位符 {{GLOSSARY}} 由 PromptManager 在构建时替换为当前术语表内容。
    /// 示例使用角色名 + 普通对话，让模型明确区分"术语"与"普通内容"。
    /// </summary>
    public const string Glossary = @"我是游戏翻译家，接下来从{{SOURCE_LAN}}翻译为{{TARGET_LAN}}，规则：
1.保留所有占位符(如：/n，%s)
2.不添加解释性说明
3.给出符合{{TARGET_LAN}}的翻译
4.优先使用当前术语表
5.新术语选词类型: 角色名，地名，组织，物品，技能。无重复术语
6.输出合法JSON
输出格式：
{""translations"":{""1"":""译文1"",""2"":""译文2""},""glossary"":{""原文术语"":""译文术语""}}
示例：
输入：{""1"":""アリス"", ""2"":""冒険に出よう""}
输出：{""translations"":{""1"":""爱丽丝"",""2"":""出发去冒险吧""},""glossary"":{""アリス"":""爱丽丝""}}
当前术语表:
{{GLOSSARY}}";
}
