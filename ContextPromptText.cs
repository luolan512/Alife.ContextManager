using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Marisa.ContextManager;

/// <summary>
/// 框架注入的官方系统消息（index[0]）的构造与剥离。
/// 单独放在这个文件里、不依赖 Alife 框架类型，是为了能脱离宿主做单元测试。
/// </summary>
public static class ContextPromptText
{
    // 官方外壳的识别标记：内容里出现它就说明这是完整系统消息，而不是裸的角色 Prompt。
    public const string OfficialPromptMarker = "这是你的人物信息";

    /// <summary>构造完整官方系统消息（名称 / 生日 / 简介 / 设定 / 私人文件夹）。</summary>
    public static string Build(string? name, DateTime birthday, string? description, string? prompt,
        string? storageKey, string? storageRoot)
    {
        var key = string.IsNullOrWhiteSpace(storageKey)
            ? Path.Combine("Character", name ?? "")
            : storageKey;
        var privateFolder = Path.Combine(storageRoot ?? "", key, "Storage");
        return $"""
                         这是你的人物信息：
                         - 名称：{name}
                         - 生日：{birthday}
                         - 简介：{description}
                         - 设定：
                         {prompt}

                         这是你的私人文件夹：
                         {privateFolder}
                         """;
    }

    /// <summary>
    /// 从官方系统消息里取出「设定」正文；内容不是官方外壳（用户自定义）时原样返回。
    /// 「本地覆盖」写回 index.json 的 Prompt 时必须用它——框架会再套一层同样的外壳，
    /// 不剥离就会出现两份人物信息。
    /// </summary>
    public static string ExtractPromptBody(string? text)
    {
        text ??= "";
        if (string.IsNullOrWhiteSpace(text)) return "";
        if (!text.Contains(OfficialPromptMarker)) return text;
        var match = Regex.Match(text,
            @"-[ \t]*设定[：:][ \t]*\r?\n(?<body>[\s\S]*?)(?=\r?\n[ \t]*\r?\n[ \t]*这是你的私人文件夹[：:]|$)");
        return match.Success ? match.Groups["body"].Value.Trim() : text;
    }

    /// <summary>
    /// 旧计划里的「角色设定」模块只存了 index.json 的裸 Prompt，这里统一升级为完整官方系统
    /// 消息，让「插件覆盖」下能把框架注入的人物信息一起改写。返回被升级的模块数量。
    /// 只有“内容恰好等于裸 Prompt 且不含官方外壳”才升级（避免覆盖用户自己写的内容）；
    /// 内容为空（用户主动「重置为空白」）保持为空。
    /// </summary>
    public static int UpgradeNativeModules(IEnumerable<ContextPlanModule>? modules, string? rawPrompt, string? officialPrompt)
    {
        if (modules == null) return 0;
        var raw = (rawPrompt ?? "").Trim();
        if (raw.Length == 0) return 0;
        if (string.IsNullOrWhiteSpace(officialPrompt)) return 0;
        var count = 0;
        foreach (var module in modules.Where(m => m.Source == "native"))
        {
            var content = (module.Content ?? "").Trim();
            if (content.Length == 0) continue;
            if (content.Contains(OfficialPromptMarker)) continue;
            if (!string.Equals(content, raw, StringComparison.Ordinal)) continue;
            module.Content = officialPrompt;
            count++;
        }
        return count;
    }
}
