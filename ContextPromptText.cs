using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.SemanticKernel;

namespace Marisa.ContextManager;

/// <summary>
/// 框架注入的官方系统消息（index[0]）的构造与剥离。
/// 单独放在这个文件里、不依赖 Alife 框架类型，是为了能脱离宿主做单元测试。
/// </summary>
public static class ContextPromptText
{
    public static string AnchorName(string? key)
    {
        var match = Regex.Match(key ?? "", @"^live-system\|system\|name:(.*?)(?:#hash:|~\d+$|$)");
        return match.Success ? match.Groups[1].Value : "";
    }

    public static void NormalizeFrameworkNames(IEnumerable<ContextPlanModule> modules)
    {
        foreach (var module in modules.Where(m => m.Source == "framework"))
        {
            var name = AnchorName(module.AnchorKey);
            if (name.Length == 0) continue;
            module.Name = name;
            // Historical index drift corrupted Content independently of OriginalContent.
            // Repair only proven cross-module substitutions, never ordinary user edits.
            var contentName = LiveSystemName(module.Content);
            if (contentName.Length > 0 && contentName != name
                && LiveSystemName(module.OriginalContent) == name && !module.Truncated)
            {
                module.Content = module.OriginalContent;
                ContextTrace.Write($"framework content repaired: {contentName} -> {name}");
            }
        }
    }

    public static Dictionary<int, string> HistoryAnchors(IReadOnlyList<ChatMessageContent> history)
        => ComputeUniqueAnchorKeys(history.Select((m, i) => (
            m.Role.Label == "system" ? "live-system" : "live-history", m.Role.Label,
            m.Content, m.Role.Label == "system" && i == 0)).ToList());

    // Resolve the whole plan once: each live message can belong to at most one override.
    // Strict keys (including ~N) reserve their targets before legacy entries are migrated.
    public static Dictionary<ContextPlanModule, int> ResolveFrameworkIndices(
        IReadOnlyList<ContextPlanModule> modules, IReadOnlyList<ChatMessageContent> history)
    {
        var keys = HistoryAnchors(history);
        var result = new Dictionary<ContextPlanModule, int>();
        var used = new HashSet<int>();
        var framework = modules.Where(m => m.Source == "framework").ToList();
        bool Legacy(ContextPlanModule m) => string.IsNullOrEmpty(m.AnchorKey)
            || (AnchorName(m.AnchorKey).Length > 0 && !m.AnchorKey.Contains("#hash:"));
        foreach (var m in framework.Where(m => !Legacy(m)))
        {
            var matches = keys.Where(p => p.Value == m.AnchorKey && !used.Contains(p.Key)).ToList();
            if (matches.Count == 1) { result[m] = matches[0].Key; used.Add(matches[0].Key); }
        }
        foreach (var m in framework.Where(Legacy))
        {
            var name = AnchorName(m.AnchorKey);
            if (name.Length == 0 && m.OriginalRole == "system")
                name = LiveSystemName(m.OriginalContent);
            if (name.Length == 0 && m.OriginalRole == "system")
            {
                var display = Regex.Replace(m.Name ?? "", @"\s*#\d+\s*$", "").Trim();
                if (Regex.IsMatch(display, @"^[A-Za-z_][A-Za-z0-9_.]*$")) name = display;
            }
            var candidates = Enumerable.Range(0, history.Count).Where(i => !used.Contains(i)
                && history[i].Role.Label == m.OriginalRole
                && (name.Length == 0 || LiveSystemName(history[i].Content) == name)).ToList();
            var exact = candidates.Where(i => !string.IsNullOrEmpty(m.OriginalContent)
                && history[i].Content == m.OriginalContent).ToList();
            int at = -1;
            if (exact.Count == 1) at = exact[0];
            else if (exact.Count > 1 && exact.Contains(m.TargetIndex)) at = m.TargetIndex;
            else if (exact.Count > 1) at = exact[0]; // identical text, consume once in plan order
            else if (name.Length > 0 && candidates.Count == 1) at = candidates[0];
            // Unknown or ambiguous identity stays unresolved. Never trust a stale index alone.
            if (at >= 0) { result[m] = at; used.Add(at); }
        }
        return result;
    }

    public static void MigrateFrameworkModules(ContextPlan plan, IReadOnlyList<ChatMessageContent> history)
    {
        var keys = HistoryAnchors(history);
        foreach (var pair in ResolveFrameworkIndices(plan.Modules, history))
        {
            pair.Key.AnchorKey = keys[pair.Value];
            pair.Key.TargetIndex = pair.Value;
        }
        NormalizeFrameworkNames(plan.Modules);
    }

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

    // ── 实时消息的「身份指纹」 ────────────────────────────────────────────────
    // 为什么需要它：装配计划里的 framework 模块以前只存「消息在 ChatHistory 数组里第几位」
    // （TargetIndex）。下标不是身份 —— 对话历史一更新（前部被裁剪、中间插入新消息），
    // 同一条消息的下标就变了，而计划里记的还是旧值，于是「模块名字」与「模块内容」
    // 配到了两条不同的消息。
    //
    // 指纹 = 来源 + 角色 + 语义标识。语义标识分两种：
    //   · 系统消息：优先用「功能说明」里那个模块名（稳定、可读、不受正文改动影响）；
    //     没有模块名（例如角色设定）就退回正文前缀哈希。
    //   · 对话消息：正文前缀哈希 + 角色。
    // ⚠️ **刻意不包含下标** —— 包含下标就等于没修。
    //
    // 这几个函数放在这个文件里（而不是 ContextManagerRuntime），是因为它们是纯字符串逻辑、
    // 不依赖 Alife 框架；放这里测试工程才能直接断言它们，而不必启动宿主。

    /// <summary>从提示词正文里抠出「功能说明」的模块名（没有就是空串）。</summary>
    public static string LiveSystemName(string? text)
    {
        var match = Regex.Match(text ?? "", @"^\[功能说明\(([^)]+)\)\]");
        return match.Success ? match.Groups[1].Value : "";
    }

    // ── 消息归属「区域」的判定 ────────────────────────────────────────────────
    // 装配要把每条消息放进四个区域之一：system / features / memory / chat。
    //
    // ⚠️ 曾经的判定是**看下标**：`position == 0 → system`，`position > 0 且是 system 消息
    // → features`，其余 → chat。那个写法有个隐含假设 —— 「所有 system 消息都连续排在开头」。
    // 但 `Interactor.Prompt` 把插件的功能说明**插在最后一个 system 消息之后**，
    // 于是「中途插入一条 system 消息」会让后面的消息全部错位分区；更糟的是记忆存档
    // （`[记忆存档(...)]`）也被判成 features，它在计划里的改写会被静默丢弃。
    //
    // 现在的判定**只看消息自身**（角色 + 正文特征），与它在数组里排第几完全无关：
    //   角色设定（历史第一条，且是 system）        → system
    //   system 且以 `[功能说明(...)]` 开头           → features
    //   以 `[记忆存档(` 开头                          → memory
    //   其余                                         → chat
    // 判断函数放在这里（纯字符串逻辑、不依赖 Alife 框架），装配侧与前端才能共用同一套规则。
    //
    // ⚠️ `[功能说明(...)]` 与 `[记忆存档(...)]` 的识别必须放在下面 `SourceGroup` 里统一做，
    // 不要各写一份 —— 两份判定一旦漂移，装配与界面就会对同一条消息给出不同的区域。

    /// <summary>长期记忆存档消息的识别前缀（由 MemoryService 注入）。</summary>
    public const string MemoryMarker = "[记忆存档(";

    /// <summary>
    /// 判定一条实时消息属于哪个区域。<b>只看消息自身，不看下标</b>（第 <c>index</c> 个参数
    /// 仅用于「历史第一条永远是角色设定」这一条特例）。
    /// <para>返回 <c>system</c> / <c>features</c> / <c>memory</c> / <c>chat</c> 之一，
    /// 与 <see cref="ContextCompiler.Groups"/> 的取值一致。</para>
    /// </summary>
    public static string SourceGroup(int index, string role, string? text)
    {
        var body = text ?? "";
        if (role != "system") return "chat";
        if (index == 0) return "system";                       // 角色设定：永远是第一条
        if (LiveSystemName(body).Length > 0) return "features"; // 插件注册的「功能说明」
        if (body.StartsWith(MemoryMarker, StringComparison.Ordinal)) return "memory";
        // 用户自己加的 system 模块（或其它插件注入的裸 system 消息）：归 system。
        // 不归 features —— features 是「功能说明」专列，混进来会让「本区域顺序」失去意义。
        return "system";
    }

    /// <summary>正文前缀的短哈希（16 位十六进制）。前缀取 160 字是权衡：够长以避免不同消息
    /// 撞车，够短以避免「消息被追加了一句话」就让整条换身份。</summary>
    public static string ShortHash(string? text)
    {
        var body = text ?? "";
        var head = body.Length > 160 ? body.Substring(0, 160) : body;
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(head));
        return Convert.ToHexString(bytes).Substring(0, 16).ToLowerInvariant();
    }

    /// <summary>
    /// 生成实时消息的身份指纹。<paramref name="isCharacterPrompt"/> 为 true 时用固定的
    /// <c>prompt</c> 标记（历史第一条永远是角色设定，给它一个可读的具名 token 便于排查）。
    ///
    /// <para><b>为什么 token 里同时带模块名和正文哈希（第二批修复）</b></para>
    ///
    /// 早先的实现是「有模块名就<b>只</b>用模块名」。这在两种真实情况下会撞车：
    ///
    /// <list type="number">
    /// <item><b>同一模块注入多条说明</b>：一个插件可以往历史里插多条
    /// <c>[功能说明(XXX)]</c>（例如 Toolkit 按分层暴露，每层一条）。它们的模块名相同，
    /// 指纹就完全相同 —— 配对函数用的是 <c>FirstOrDefault</c>，于是后面几条卡片
    /// 全部命中第一条模块，表现为「模块名对不上 / 互相移位」。</item>
    /// <item><b>正文被改写</b>：其他插件更新了自己的说明文字。正文一变，
    /// 若指纹只认名字就仍能命中（这点是对的），但如果名字取的边界不同，
    /// 就可能配到相邻的另一条，把别人模块的正文覆盖掉。</item>
    /// </list>
    ///
    /// 现在 token 由两段组成，<b>名字用于「同一模块的多条之间聚类」，
    /// 正文哈希用于「同一条消息前后稳定」</b>：
    /// <c>name:XXX#hash:abcd…</c>。这样：
    /// <list type="bullet">
    /// <item>同一模块的不同条目 → 名字相同但哈希不同 → 指纹不同（不再互相覆盖）；</item>
    /// <item>同一条消息正文被追加/微调 → 哈希落在前 160 字之外时指纹不变（仍能命中）；</item>
    /// <item>正文改在前 160 字内 → 指纹变了 → 按「新消息」处理。<b>这是有意的保守选择</b>：
    /// 宁可认不出来（原样保留），也绝不认错（拿别人的正文顶上）。</item>
    /// </list>
    /// </summary>
    public static string ComputeAnchorKey(string sourceKey, string role, string? text,
        bool isCharacterPrompt = false)
    {
        if (isCharacterPrompt)
            return $"{sourceKey}|{role}|prompt";
        var name = sourceKey == "live-system" ? LiveSystemName(text) : "";
        var digest = ShortHash(text);
        var token = name.Length > 0 ? $"name:{name}#hash:{digest}" : "hash:" + digest;
        return $"{sourceKey}|{role}|{token}";
    }

    /// <summary>
    /// 从一组实时消息里计算指纹，并保证<b>结果两两不同</b>。
    ///
    /// <para>即使 <see cref="ComputeAnchorKey"/> 已经带上正文哈希，仍然可能出现两条
    /// 逐字相同的消息（比如同一个插件被加载了两次、或复制粘贴出的重复条目）。
    /// 指纹一旦重复，「配对」就退化成「按先后顺序猜」，卡片又会互相串位。</para>
    ///
    /// <para>这里对重复的指纹追加 <c>~1</c>、<c>~2</c>… 做消歧。<b>顺序取的是消息在
    /// 历史里的先后</b>，所以只要这两条重复消息的相对顺序不变，它们的指纹就稳定 ——
    /// 不会因为前面多了一条无关消息就整体错位。</para>
    ///
    /// <para>返回的字典以「消息在 <paramref name="texts"/> 里的下标」为键。</para>
    /// </summary>
    public static Dictionary<int, string> ComputeUniqueAnchorKeys(
        IReadOnlyList<(string SourceKey, string Role, string? Text, bool IsCharacterPrompt)> messages)
    {
        var result = new Dictionary<int, string>();
        var used = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < messages.Count; i++)
        {
            var m = messages[i];
            var key = ComputeAnchorKey(m.SourceKey, m.Role, m.Text, m.IsCharacterPrompt);
            if (used.TryGetValue(key, out var seen))
            {
                used[key] = seen + 1;
                key = key + "~" + (seen + 1);
            }
            else used[key] = 0;
            result[i] = key;
        }
        return result;
    }
}
