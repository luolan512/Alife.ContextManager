using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Marisa.ContextManager;

/// <summary>
/// 给角色（AI）用的上下文操作层 —— 纯逻辑，不碰 IPC、不碰审批。
///
/// 为什么要单独拆一层：界面走的是「渲染进程 → IPC → Runtime」，
/// 角色走的是「聊天 → XmlHandler → 这里」。两条入口的**落地动作必须是同一套**
/// （同一个 <see cref="ContextPlanService"/>、同一份计划文件），否则界面看到的内容
/// 和角色以为的内容会分叉。所以这里只做「读计划 → 改计划 → 写计划」，
/// 具体怎么让改动生效（插件覆盖 / 本地覆盖）交给 Runtime 的 ApplyPlanCore。
///
/// 三条硬规则：
/// <list type="number">
/// <item>看得到 ≠ 改得动。任何写操作都要先过 <see cref="ContextAgentGuard"/>；</item>
/// <item>不许凭空造正文。<c>Truncated</c> 的模块一律先从磁盘回填（这里只读磁盘，所以天然是真内容）；</item>
/// <item>不替角色做决定。返回的永远是「事实 + 可选动作」，不是「我帮你改好了」。</item>
/// </list>
/// </summary>
public sealed class ContextAgentOps
{
    readonly ContextPlanService planService;
    readonly Func<string, JObject?> readIndex;
    readonly Func<string, ContextPlan, string> renderPreview;
    readonly string presetDirectory;

    /// <summary>
    /// 「计划已经落盘」的通知口。**由 Runtime 注入**（本层不碰 IPC —— 见类注释）。
    ///
    /// <para><b>为什么需要它：</b>这一层改完计划只写磁盘，而用户界面那一侧
    /// （<c>plan-state</c> / <c>state</c> 报文）不会自己知道。缺了这个通知，
    /// 角色新增/修改/删除模块之后，用户盯着的那扇窗口会一直停在旧样子，必须关掉重开才看得到。
    /// 这正是「AI 改的模块在管理界面里不实时刷新 / 新模块根本不显示」的原因。</para>
    ///
    /// <para><b>三条约定（都由测试锁定）：</b></para>
    /// <list type="number">
    /// <item><b>必须在落盘之后调</b> —— 通知里若去读计划（Runtime 就会读），读到的必须是新内容；</item>
    /// <item><b>只在真正改动了才调</b> —— 读操作（Outline / ReadModule / Preview）与失败的写操作一律不调；</item>
    /// <item><b>它抛异常绝不能影响这次写操作的结果</b> —— 计划已经落盘了，那是真东西；
    /// 用户在没开窗口时操作，通知里会拿不到窗口，不能因此把「已保存」报成失败。</item>
    /// </list>
    /// 每一条都在 <see cref="Notify"/> 里兜住了，调用方直接调即可。
    /// </summary>
    readonly Action<string, ContextPlan>? onPlanChanged;

    /// <param name="renderPreview">把计划渲染成最终报文的委托（由 Runtime 提供，复用同一套宏展开）。</param>
    /// <param name="presetDirectory">角色快照目录（Storage/ContextManager/CharacterPresets）。</param>
    /// <param name="onPlanChanged">计划落盘后的通知（owner, 已落盘的计划）。可由 Runtime 注入。
    /// **默认 null = 不通知**，方便测试与「没有窗口」的场景。</param>
    public ContextAgentOps(
        ContextPlanService planService,
        Func<string, JObject?> readIndex,
        Func<string, ContextPlan, string> renderPreview,
        string presetDirectory,
        Action<string, ContextPlan>? onPlanChanged = null)
    {
        this.planService = planService;
        this.readIndex = readIndex;
        this.renderPreview = renderPreview;
        this.presetDirectory = presetDirectory;
        this.onPlanChanged = onPlanChanged;
    }

    /// <summary>
    /// 落盘之后的统一出口：先存盘，再通知；通知里的任何异常都只记日志、不外抛。
    ///
    /// <para><b>顺序不可颠倒。</b>通知方（Runtime）会去读计划并算 fingerprint / 名字，
    /// 先通知后存盘的话，它读到的是**旧**计划，界面会闪一下旧内容再等下一次推送 ——
    /// 而且测试锁定了「通知发生时磁盘上已经是新计划」。</para>
    /// </summary>
    void SaveAndNotify(string owner, ContextPlan plan)
    {
        planService.SavePlan(owner, plan);
        if (onPlanChanged == null) return;
        try { onPlanChanged(owner, plan); }
        catch { /* 通知只是「让窗口跟上」，失败不该把已经成功的改动报成失败 */ }
    }

    // ───────────────────────────── 查看 ─────────────────────────────

    /// <summary>
    /// 列出一个角色当前装配计划的骨架：模式、模块数、每个模块的 Id / 名称 / 身份 / 区域 /
    /// 开关 / 字数 / 来源。**不含正文** —— 正文按需用 <see cref="ReadModule"/> 单取，
    /// 一条报文塞 143 个模块的全文会把 IPC 桥顶断（2026-09-30 的教训）。
    /// </summary>
    public ContextAgentOpsResult<AgentPlanOutline> Outline(string owner)
    {
        var plan = planService.GetOrCreatePlan(owner);
        ContextCompiler.Validate(plan);
        var outline = new AgentPlanOutline
        {
            Owner = owner,
            Mode = plan.Mode,
            ModeLabel = DescribeMode(plan.Mode),
            ApplyMacros = plan.ApplyMacros,
            AutoMacros = plan.AutoMacros,
            UserName = plan.UserName,
            UpdatedAt = plan.UpdatedAt,
            ModuleCount = plan.Modules.Count,
            EnabledCount = plan.Modules.Count(m => m.Enabled),
            TotalChars = plan.Modules.Where(m => m.Enabled).Sum(m => (m.Content ?? "").Length),
            Modules = plan.Modules.Select((m, i) => new AgentModuleBrief
            {
                Index = i,
                Id = m.Id,
                Name = m.Name,
                Role = m.Role,
                Group = m.Group,
                Source = m.Source,
                Enabled = m.Enabled,
                Constant = m.Constant,
                Chars = (m.Content ?? "").Length,
                Preview = OneLine(m.Content, 80)
            }).ToList()
        };
        var msg = $"{owner} 的装配计划：{outline.ModuleCount} 个模块（{outline.EnabledCount} 个参与发送，"
            + $"共约 {outline.TotalChars} 字），覆盖方式 = {outline.ModeLabel}。";
        return ContextAgentOpsResult<AgentPlanOutline>.Succeed(outline).With(msg);
    }

    /// <summary>取一个模块的完整正文。按 Id 优先，其次按名字（支持包含匹配）。</summary>
    public ContextAgentOpsResult<ContextPlanModule> ReadModule(string owner, string? id, string? name)
    {
        var plan = planService.GetOrCreatePlan(owner);
        var module = FindModule(plan, id, name);
        if (module == null)
            return ContextAgentOpsResult<ContextPlanModule>.Fail(BuildNotFoundHint(plan, id, name));
        return ContextAgentOpsResult<ContextPlanModule>.Succeed(module)
            .With($"模块「{module.Name}」（{module.Role} / {module.Group} / {(module.Enabled ? "参与发送" : "已停用")}，"
                + $"正文 {(module.Content ?? "").Length} 字）。");
    }

    /// <summary>把整个计划渲染成最终会发给模型的样子（只读，用来让角色确认自己改对了没）。</summary>
    public ContextAgentOpsResult<string> Preview(string owner, int maxChars = 8000)
    {
        var plan = planService.GetOrCreatePlan(owner);
        var warnings = new List<string>();
        try
        {
            var text = renderPreview(owner, plan) ?? "";
            var total = text.Length;
            if (maxChars > 0 && total > maxChars)
                return ContextAgentOpsResult<string>.Succeed(text[..maxChars])
                    .With($"以下是渲染结果的前 {maxChars} 字（完整共 {total} 字）。");
            return ContextAgentOpsResult<string>.Succeed(text)
                .With($"以下是完整渲染结果（共 {total} 字）。");
        }
        catch (Exception ex)
        {
            return ContextAgentOpsResult<string>.Fail("渲染预览失败：" + ex.Message);
        }
    }

    // ─────────────────────────── 模块增删改 ───────────────────────────

    /// <summary>
    /// 新增一个模块。写进磁盘上的计划（<c>Plans/&lt;角色&gt;.json</c>），不立刻生效 ——
    /// 生效要另走「应用」，因为应用是**会影响真实对话**的动作，必须单独确认。
    /// </summary>
    public ContextAgentOpsResult<ContextPlanModule> AddModule(string owner, AgentModuleDraft draft)
    {
        if (string.IsNullOrWhiteSpace(draft.Name)) draft.Name = "未命名模块";
        var plan = planService.GetOrCreatePlan(owner);
        var module = new ContextPlanModule
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = draft.Name.Trim(),
            Role = NormalizeRole(draft.Role),
            Content = draft.Content ?? "",
            Enabled = draft.Enabled,
            Group = NormalizeGroup(draft.Group, draft.Role),
            Source = "agent",
            Constant = draft.Constant,
            Keywords = Clean(draft.Keywords),
            SecondaryKeywords = Clean(draft.SecondaryKeywords),
            Selective = draft.Selective,
            CaseSensitive = draft.CaseSensitive
        };
        var at = InsertIndex(plan, draft.Index, draft.Group);
        plan.Modules.Insert(at, module);
        SaveAndNotify(owner, plan);
        return ContextAgentOpsResult<ContextPlanModule>.Succeed(module)
            .With($"已新增模块「{module.Name}」到第 {at + 1} 位（现在共 {plan.Modules.Count} 个）。改动还没生效 —— 需要「应用」之后才会影响真实对话。");
    }

    /// <summary>
    /// 改一个模块。只改传进来的字段 —— 没传的保持原值（这是「编辑」不是「替换」）。
    /// </summary>
    public ContextAgentOpsResult<ContextPlanModule> UpdateModule(string owner, string? id, string? name, AgentModulePatch patch)
    {
        var plan = planService.GetOrCreatePlan(owner);
        var module = FindModule(plan, id, name);
        if (module == null)
            return ContextAgentOpsResult<ContextPlanModule>.Fail(BuildNotFoundHint(plan, id, name));

        var changed = new List<string>();
        if (patch.Name != null) { module.Name = patch.Name.Trim(); changed.Add("名称"); }
        if (patch.Role != null) { module.Role = NormalizeRole(patch.Role); changed.Add("身份"); }
        if (patch.Content != null) { module.Content = patch.Content; changed.Add("正文"); }
        if (patch.Enabled.HasValue) { module.Enabled = patch.Enabled.Value; changed.Add(patch.Enabled.Value ? "启用" : "停用"); }
        if (patch.Group != null) { module.Group = NormalizeGroup(patch.Group, module.Role); changed.Add("区域"); }
        if (patch.Constant.HasValue) { module.Constant = patch.Constant.Value; changed.Add("常驻"); }
        if (patch.Selective.HasValue) { module.Selective = patch.Selective.Value; changed.Add("选择性"); }
        if (patch.CaseSensitive.HasValue) { module.CaseSensitive = patch.CaseSensitive.Value; changed.Add("区分大小写"); }
        if (patch.Keywords != null) { module.Keywords = Clean(patch.Keywords); changed.Add("关键词"); }
        if (patch.SecondaryKeywords != null) { module.SecondaryKeywords = Clean(patch.SecondaryKeywords); changed.Add("次要关键词"); }
        if (patch.Index.HasValue)
        {
            var from = plan.Modules.IndexOf(module);
            var to = Math.Clamp(patch.Index.Value, 0, plan.Modules.Count - 1);
            if (from != to) { plan.Modules.RemoveAt(from); plan.Modules.Insert(to, module); changed.Add($"顺序 {from + 1}→{to + 1}"); }
        }
        if (changed.Count == 0)
            return ContextAgentOpsResult<ContextPlanModule>.Fail("没有传任何要修改的字段。可改：名称 / 身份 / 正文 / 启用 / 区域 / 顺序 / 关键词 / 常驻 / 选择性。")
                .With("模块 Id：" + module.Id);

        SaveAndNotify(owner, plan);
        return ContextAgentOpsResult<ContextPlanModule>.Succeed(module)
            .With($"已修改模块「{module.Name}」：{string.Join("、", changed)}。改动还没生效 —— 需要「应用」之后才会影响真实对话。");
    }

    /// <summary>删一个模块。删除是**不可逆**的（计划里没有回收站），所以要审批。</summary>
    public ContextAgentOpsResult<ContextPlanModule> DeleteModule(string owner, string? id, string? name)
    {
        var plan = planService.GetOrCreatePlan(owner);
        var module = FindModule(plan, id, name);
        if (module == null)
            return ContextAgentOpsResult<ContextPlanModule>.Fail(BuildNotFoundHint(plan, id, name));
        if (plan.Modules.Count <= 1)
            return ContextAgentOpsResult<ContextPlanModule>.Fail("这是计划里最后一个模块，删掉后装配内容会完全为空。如果确实要清空，请让用户在插件窗口里点「重置」。");
        plan.Modules.Remove(module);
        SaveAndNotify(owner, plan);
        return ContextAgentOpsResult<ContextPlanModule>.Succeed(module)
            .With($"已删除模块「{module.Name}」（正文 {(module.Content ?? "").Length} 字）。剩余 {plan.Modules.Count} 个。改动还没生效 —— 需要「应用」之后才会影响真实对话。");
    }

    /// <summary>
    /// 把一批模块**整批**追加进计划（导入用）。与逐个 <see cref="AddModule"/> 的区别：
    /// 只落盘一次、只在最后给一条汇总消息，避免「导入 30 条世界书 = 30 次磁盘写 + 30 条噪音」。
    ///
    /// <para><b>同名去重</b>：按「名称 + 正文前 200 字」判重。重复导入同一张卡 / 同一本书时
    /// 只跳过重复项、不报错 —— 导入是「加上去」，不是「换掉」。</para>
    /// </summary>
    public ContextAgentOpsResult<List<ContextPlanModule>> ImportModules(string owner, IReadOnlyList<ContextPlanModule> modules)
    {
        if (modules.Count == 0)
            return ContextAgentOpsResult<List<ContextPlanModule>>.Fail("没有要导入的模块。");

        var plan = planService.GetOrCreatePlan(owner);
        var seen = new HashSet<string>(plan.Modules.Select(DuplicateKey), StringComparer.Ordinal);
        var added = new List<ContextPlanModule>();
        var skipped = 0;
        foreach (var module in modules)
        {
            if (string.IsNullOrWhiteSpace(module.Content)) { skipped++; continue; }
            if (!seen.Add(DuplicateKey(module))) { skipped++; continue; }
            module.Id = string.IsNullOrWhiteSpace(module.Id) ? Guid.NewGuid().ToString("N") : module.Id;
            module.Name = string.IsNullOrWhiteSpace(module.Name) ? "导入模块" : module.Name.Trim();
            module.Role = NormalizeRole(module.Role);
            module.Group = NormalizeGroup(module.Group, module.Role);
            plan.Modules.Add(module);
            added.Add(module);
        }
        if (added.Count > 0) SaveAndNotify(owner, plan);

        var byGroup = added.GroupBy(m => m.Group)
            .OrderBy(g => Array.IndexOf(ContextCompiler.Groups, g.Key))
            .Select(g => $"{g.Key} {g.Count()} 个");
        var message = added.Count > 0
            ? $"已导入 {added.Count} 个模块（{string.Join("、", byGroup)}），共 {added.Sum(m => (m.Content ?? "").Length)} 字。现在计划里共 {plan.Modules.Count} 个模块。"
            : "没有新增任何模块。";
        if (skipped > 0) message += $"\n跳过 {skipped} 个（重复、或正文为空）。";
        message += "\n\n提示：改动还没生效，需要再调用「应用上下文」。";
        return added.Count > 0
            ? ContextAgentOpsResult<List<ContextPlanModule>>.Succeed(added).With(message)
            : ContextAgentOpsResult<List<ContextPlanModule>>.Fail(message);
    }

    /// <summary>导入判重键：名称 + 正文前 200 字。</summary>
    static string DuplicateKey(ContextPlanModule m)
    {
        var body = (m.Content ?? "").Trim();
        if (body.Length > 200) body = body[..200];
        return (m.Name ?? "").Trim() + "\u0000" + body + "\u0000" + m.Group;
    }

    // ─────────────────────────── 导入（角色卡 / 世界书 / 预设） ───────────────────────────

    /// <summary>
    /// 从**文件正文**解析出一批上下文模块（角色卡 / 世界书 / 预设），供 AI 导入。
    ///
    /// <para><b>为什么在 ops 层做</b>：解析本身是纯逻辑（<see cref="TavernImport"/>），
    /// 但「变成哪些模块、叫什么名字、放哪个区域」需要和普通模块同一套 <see cref="NormalizeGroup"/>
    /// 规则 —— 放在这里，AI 导入与界面导入才会得到一致的结果。</para>
    ///
    /// <para><b>只解析成模块，不写回角色目录</b>（用户明确要求）。导入进去的模块
    /// 来源标记为 <c>preset</c> / <c>worldbook</c>，和界面导入一致：
    /// 关掉插件就复原，不会污染角色文件。</para>
    /// </summary>
    /// <param name="kind">card / worldbook / preset；留空 = 按内容自动识别。</param>
    /// <param name="text">文件正文（JSON）。</param>
    /// <param name="path"><paramref name="text"/> 为空时改从这里读文件。</param>
    public ContextAgentOpsResult<List<ContextPlanModule>> ParseImport(string kind, string? text, string? path)
    {
        string json = text ?? "";
        JObject? pngCard = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            if (string.IsNullOrWhiteSpace(path))
                return ContextAgentOpsResult<List<ContextPlanModule>>.Fail("要导入什么？请给出文件路径（path），或直接把 JSON 正文写在标签里。");
            try
            {
                if (!File.Exists(path))
                    return ContextAgentOpsResult<List<ContextPlanModule>>.Fail("找不到文件：" + path);
                // .png 角色卡要从 PNG 的 tEXt 块里取，不能当文本读。
                if (Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase))
                    pngCard = TavernImport.ReadCard(path);
                else
                    json = File.ReadAllText(path);
            }
            catch (Exception ex)
            {
                return ContextAgentOpsResult<List<ContextPlanModule>>.Fail("读取失败：" + ex.Message);
            }
        }

        string declared = (kind ?? "").Trim().ToLowerInvariant();
        var use = "card";
        if (pngCard == null)
        {
            JObject root;
            try { root = JObject.Parse(json); }
            catch (Exception ex)
            {
                return ContextAgentOpsResult<List<ContextPlanModule>>.Fail("这段内容不是合法的 JSON：" + ex.Message);
            }
            // 形状优先于声明：AI 常常把世界书说成「角色卡」。先看长什么样，再决定怎么解析。
            var shape = DetectImportShape(root);
            if (declared.Length > 0 && shape != "unknown" && !ShapeMatches(declared, shape))
                return ContextAgentOpsResult<List<ContextPlanModule>>.Fail(
                    $"你说的是「{declared}」，但这段内容的形状看起来是「{shape}」。"
                    + "请确认 kind（card / worldbook / preset），或留空让插件自己判断。");
            use = shape != "unknown" ? shape : declared;
            if (use.Length == 0)
                return ContextAgentOpsResult<List<ContextPlanModule>>.Fail(
                    "认不出这是什么：既不像角色卡、也不像世界书、也不像预设。"
                    + "角色卡要有 description / personality / first_mes 等字段；世界书要有 entries；预设要有 prompts 或 prompt_order。");
            pngCard = root;
        }

        try
        {
            var modules = use switch
            {
                "card" => ParseImportFromCard(pngCard),
                "worldbook" => TavernImport.World(pngCard).Select(m => { m.Source = "worldbook"; return m; }).ToList(),
                _ => TavernImport.Preset(pngCard)
            };
            if (modules.Count == 0)
                return ContextAgentOpsResult<List<ContextPlanModule>>.Fail($"解析成功，但里面没有任何可用的内容（{use}）。");
            foreach (var m in modules)
            {
                m.Id = Guid.NewGuid().ToString("N");
                m.Role = NormalizeRole(m.Role);
                m.Group = NormalizeGroup(m.Group, m.Role);
            }
            return ContextAgentOpsResult<List<ContextPlanModule>>.Succeed(modules)
                .With($"从{ShapeLabel(use)}里解析出 {modules.Count} 个模块。");
        }
        catch (Exception ex)
        {
            return ContextAgentOpsResult<List<ContextPlanModule>>.Fail("解析失败：" + ex.Message);
        }
    }

    /// <summary>角色卡 → 模块：6 个标准提示词字段 + 额外开场白 + （可选）卡内世界书。</summary>
    static List<ContextPlanModule> ParseImportFromCard(JObject root)
    {
        // 酒馆 v2/v3 把字段放在 spec:char 的 data 里；也有裸放顶层的旧卡。
        var card = root["data"] as JObject ?? root;
        var result = new List<ContextPlanModule>();
        foreach (var (key, label, _) in TavernImport.CardFields)
        {
            var value = card.Value<string>(key) ?? "";
            if (string.IsNullOrWhiteSpace(value)) continue;
            result.Add(new ContextPlanModule
            {
                Name = "角色卡 · " + label, Content = value, Role = "system", Group = "system",
                Enabled = true, Source = "preset"
            });
        }
        // first_mes 之外的开场白：各自成模块（与界面导入一致）。
        foreach (var greeting in TavernImport.AlternateGreetings(card))
            result.Add(new ContextPlanModule
            {
                Name = "角色卡 · " + greeting.Label, Content = greeting.Text, Role = "system",
                Group = "system", Enabled = false, Source = "preset"
            });
        foreach (var entry in TavernImport.World(card["character_book"]))
        {
            entry.Source = "worldbook";
            result.Add(entry);
        }
        return result;
    }

    /// <summary>看一段 JSON 像什么：card / worldbook / preset / unknown。</summary>
    static string DetectImportShape(JObject root)
    {
        var card = root["data"] as JObject ?? root;
        // 角色卡的判据：至少有一个酒馆标准提示词字段。
        if (TavernImport.CardFields.Any(f => !string.IsNullOrWhiteSpace(card.Value<string>(f.Key))))
            return "card";
        if (root["character_book"] != null || root["entries"] != null || root["Entries"] != null)
            return "worldbook";
        if (root["prompts"] != null || root["prompt_order"] != null || root["system_prompt"] != null)
            return "preset";
        // 本插件自己的 WorldBook.json：只有 Entries 一个字段，上面已经覆盖；这里兜底。
        if (root.Properties().Count() == 1 && root["Entries"] is JArray) return "worldbook";
        return "unknown";
    }

    /// <summary>声明类型与实测形状是否矛盾。只拦「明确矛盾」，不拦未知。</summary>
    static bool ShapeMatches(string declared, string shape)
    {
        string Norm(string s) => s switch
        {
            "card" or "character" or "char" or "角色卡" or "角色" => "card",
            "worldbook" or "world" or "book" or "世界书" => "worldbook",
            "preset" or "预设" => "preset",
            _ => s
        };
        return Norm(declared) == Norm(shape);
    }

    static string ShapeLabel(string shape) => shape switch
    {
        "card" => "角色卡", "worldbook" => "世界书", "preset" => "预设", _ => "这段内容"
    };

    // ─────────────────────────── 快照（预设） ───────────────────────────

    /// <summary>列出一个角色已有的快照槽（含自动槽）。</summary>
    public List<AgentPresetBrief> ListPresets(string owner)
    {
        var dir = EnsurePresetDirectory();
        var ownerKey = SafeName(owner);
        var list = new List<AgentPresetBrief>();
        foreach (var file in Directory.GetFiles(dir, "*.json"))
        {
            var baseName = Path.GetFileNameWithoutExtension(file);
            var isAuto = string.Equals(baseName, ownerKey, StringComparison.OrdinalIgnoreCase);
            var isNamed = baseName.StartsWith(ownerKey + "__", StringComparison.OrdinalIgnoreCase);
            if (!isAuto && !isNamed) continue;
            try
            {
                var preset = JsonConvert.DeserializeObject<CharacterContextPreset>(File.ReadAllText(file));
                if (preset == null) continue;
                list.Add(new AgentPresetBrief
                {
                    Name = string.IsNullOrWhiteSpace(preset.Name) ? (isAuto ? owner : baseName[(ownerKey.Length + 2)..]) : preset.Name,
                    Auto = preset.Auto || isAuto,
                    UpdatedAt = preset.UpdatedAt,
                    Mode = preset.Plan?.Mode ?? "Off",
                    Modules = preset.Plan?.Modules?.Count ?? 0,
                    WorldbookEntries = preset.Sources?.WorldBookEntries ?? 0,
                    HasTavernCard = preset.Sources?.TavernCard != null
                });
            }
            catch (Exception ex) { ContextTrace.Write("agent preset list parse failed " + file + " : " + ex.Message); }
        }
        return list.OrderByDescending(s => s.Auto).ThenBy(s => s.Name, StringComparer.Ordinal).ToList();
    }

    public ContextAgentOpsResult<string> SavePreset(string owner, string name)
    {
        var plan = planService.GetOrCreatePlan(owner);
        ContextCompiler.Validate(plan);
        var file = PresetPath(owner, name);
        var auto = string.IsNullOrWhiteSpace(name) || string.Equals(name.Trim(), owner, StringComparison.OrdinalIgnoreCase);
        var preset = new CharacterContextPreset
        {
            Owner = owner,
            Name = auto ? owner : name.Trim(),
            Auto = auto,
            UpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            Plan = plan
            // ⚠️ 这里**不带 Sources**。角色存快照时不该顺手把角色卡/世界书原文复制一份 ——
            // 那是界面「导出角色记录」的语义（整个角色带走）。角色要的只是「记住这个配置」。
        };
        AtomicWrite(file, JsonConvert.SerializeObject(preset, ContextJsonSettings.Disk));
        return ContextAgentOpsResult<string>.Succeed(preset.Name)
            .With($"已保存快照「{preset.Name}」（{plan.Modules.Count} 个模块，覆盖方式 {DescribeMode(plan.Mode)}）。");
    }

    public ContextAgentOpsResult<string> DeletePreset(string owner, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || string.Equals(name.Trim(), owner, StringComparison.OrdinalIgnoreCase))
            return ContextAgentOpsResult<string>.Fail("自动快照（与角色同名的那一份）不能删 —— 它是插件恢复用的底稿。要清理命名快照请指定快照名。"
                + DescribeSlots(owner));
        var file = PresetPath(owner, name);
        if (!File.Exists(file))
            return ContextAgentOpsResult<string>.Fail($"没有名为「{name.Trim()}」的快照。" + DescribeSlots(owner));
        File.Delete(file);
        return ContextAgentOpsResult<string>.Succeed(name.Trim()).With($"已删除快照「{name.Trim()}」。");
    }

    /// <summary>
    /// 切换（装载）一份快照：把它的计划装回磁盘计划。**只装载，不生效** ——
    /// 生效是另一件事（要应用），因为那会改变角色接下来真正发出去的内容。
    /// </summary>
    public ContextAgentOpsResult<ContextPlan> LoadPreset(string owner, string name)
    {
        var file = PresetPath(owner, name);
        if (!File.Exists(file))
            return ContextAgentOpsResult<ContextPlan>.Fail($"没有名为「{name}」的快照。" + DescribeSlots(owner));
        var preset = JsonConvert.DeserializeObject<CharacterContextPreset>(File.ReadAllText(file));
        if (preset?.Plan == null)
            return ContextAgentOpsResult<ContextPlan>.Fail($"快照「{name}」里没有装配计划。");
        ContextCompiler.Validate(preset.Plan);
        SaveAndNotify(owner, preset.Plan);
        return ContextAgentOpsResult<ContextPlan>.Succeed(preset.Plan)
            .With($"已装载快照「{preset.Name}」（{preset.Plan.Modules.Count} 个模块，覆盖方式 {DescribeMode(preset.Plan.Mode)}）。改动还没生效 —— 需要「应用」之后才会影响真实对话。");
    }

    // ───────────────────────────── 工具 ─────────────────────────────

    static ContextPlanModule? FindModule(ContextPlan plan, string? id, string? name)
    {
        if (!string.IsNullOrWhiteSpace(id))
        {
            var byId = plan.Modules.FirstOrDefault(m => string.Equals(m.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));
            if (byId != null) return byId;
        }
        if (!string.IsNullOrWhiteSpace(name))
        {
            var trimmed = name.Trim();
            return plan.Modules.FirstOrDefault(m => string.Equals(m.Name, trimmed, StringComparison.OrdinalIgnoreCase))
                ?? plan.Modules.FirstOrDefault(m => m.Name?.Contains(trimmed, StringComparison.OrdinalIgnoreCase) == true);
        }
        return null;
    }

    static string BuildNotFoundHint(ContextPlan plan, string? id, string? name)
    {
        var key = !string.IsNullOrWhiteSpace(id) ? id : name;
        var hint = $"找不到模块（{(string.IsNullOrWhiteSpace(id) ? "名称" : "Id")}=\"{key}\"）。";
        if (plan.Modules.Count == 0) return hint + " 这个角色的计划目前是空的。";
        var sample = string.Join("、", plan.Modules.Take(12).Select(m => m.Name));
        return hint + $" 现有模块：{sample}{(plan.Modules.Count > 12 ? " …" : "")}。可先用「查看上下文」列出全部。";
    }

    static int InsertIndex(ContextPlan plan, int? index, string? group)
    {
        if (index.HasValue) return Math.Clamp(index.Value, 0, plan.Modules.Count);
        var normalized = NormalizeGroup(group, null);
        // 没指定位置就插到同区域的末尾，保证「系统 → 用户 → 助手」的分组不被插花。
        var last = plan.Modules.FindLastIndex(m => string.Equals(m.Group, normalized, StringComparison.OrdinalIgnoreCase));
        return last < 0 ? plan.Modules.Count : last + 1;
    }

    static string NormalizeRole(string? role)
    {
        var value = (role ?? "").Trim().ToLowerInvariant();
        return value switch
        {
            "user" => "User",
            "assistant" or "ai" or "model" => "Assistant",
            _ => "System"
        };
    }

    /// <summary>
    /// 归一化「区域」（Group）。
    ///
    /// ⚠️ 区域与身份（Role）**不是一回事**，别混：
    /// <list type="bullet">
    /// <item>Role = 这条消息以谁的名义发出（system / user / assistant）；</item>
    /// <item>Group = 它插在装配顺序的哪一段（system / features / memory / chat）。</item>
    /// </list>
    /// 合法的 Group 只有 <see cref="ContextCompiler.Groups"/> 里那四个，
    /// 写别的会在 <see cref="ContextCompiler.Validate"/> 抛「无效的模块区域」。
    /// 角色常常会把「身份」当成「区域」来说（比如 role=user 就以为 group=user），
    /// 所以这里对身份词做一次友好映射，最后兜底归到最前面的 system 段。
    /// </summary>
    static string NormalizeGroup(string? group, string? role)
    {
        var value = (group ?? "").Trim().ToLowerInvariant();
        switch (value)
        {
            case "system": case "features": case "memory": case "chat":
                return value;
            // 角色把「身份」当「区域」说时的容错映射（对齐 ContextCompiler.Compile 的分段语义）。
            case "user" or "assistant" or "ai" or "model" or "chat_history" or "history":
                return "chat";
            case "feature" or "tools" or "tool" or "functions":
                return "features";
            case "memories" or "memory_entry" or "archive":
                return "memory";
            case "sys" or "prompt" or "character" or "persona":
                return "system";
            default:
                // 空值也走这里：没指定区域 -> 按身份折算。
                return NormalizeRole(role) switch
                {
                    "User" or "Assistant" => "chat",
                    _ => "system"
                };
        }
    }

    static List<string> Clean(List<string>? list)
        => (list ?? new List<string>()).Where(k => !string.IsNullOrWhiteSpace(k)).Select(k => k.Trim()).ToList();

    public static string DescribeMode(string? mode) => (mode ?? "").Trim() switch
    {
        "Temporary" => "插件覆盖（每次请求前重排，不动角色文件）",
        "Permanent" => "本地覆盖（写进角色 index.json，插件关掉也生效）",
        _ => "关闭（走 Alife 原生装配）"
    };

    static string OneLine(string? text, int max)
    {
        text = (text ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        return text.Length <= max ? text : text[..max] + "…";
    }

    static string SafeName(string name)
    {
        var safe = Path.GetInvalidFileNameChars().Aggregate((name ?? "Preset").Trim(), (a, c) => a.Replace(c, '_'));
        return string.IsNullOrWhiteSpace(safe) ? "Preset" : safe;
    }

    static void AtomicWrite(string path, string content)
    {
        File.WriteAllText(path + ".tmp", content);
        File.Move(path + ".tmp", path, true);
    }

    string EnsurePresetDirectory()
    {
        Directory.CreateDirectory(presetDirectory);
        return presetDirectory;
    }

    string PresetPath(string owner, string? name)
        => Path.Combine(EnsurePresetDirectory(), PresetFileName(owner, name));

    /// <summary>快照文件名规则与 Runtime 完全一致（同名同规则，才能互相读得到）。</summary>
    internal static string PresetFileName(string owner, string? name)
    {
        var ownerKey = SafeName(owner);
        if (string.IsNullOrWhiteSpace(name) || string.Equals(name.Trim(), owner, StringComparison.OrdinalIgnoreCase))
            return ownerKey + ".json";
        return ownerKey + "__" + SafeName(name.Trim()) + ".json";
    }

    string DescribeSlots(string owner)
    {
        var slots = ListPresets(owner);
        if (slots.Count == 0) return " 这个角色目前没有任何快照。";
        return " 现有快照：" + string.Join("、", slots.Select(s => s.Name + (s.Auto ? "（自动）" : "")));
    }
}

// ───────────────────────────── 数据形状 ─────────────────────────────

/// <summary>计划骨架（不含正文）。</summary>
public sealed class AgentPlanOutline
{
    public string Owner { get; set; } = "";
    public string Mode { get; set; } = "Off";
    public string ModeLabel { get; set; } = "";
    public bool ApplyMacros { get; set; }
    public bool AutoMacros { get; set; }
    public string UserName { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
    public int ModuleCount { get; set; }
    public int EnabledCount { get; set; }
    public int TotalChars { get; set; }
    public List<AgentModuleBrief> Modules { get; set; } = new();
}

/// <summary>单个模块的一句话说明。</summary>
public sealed class AgentModuleBrief
{
    public int Index { get; set; }
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Role { get; set; } = "System";
    public string Group { get; set; } = "system";
    public string Source { get; set; } = "custom";
    public bool Enabled { get; set; }
    public bool Constant { get; set; }
    public int Chars { get; set; }
    public string Preview { get; set; } = "";
}

/// <summary>新增模块的入参。</summary>
public sealed class AgentModuleDraft
{
    public string Name { get; set; } = "";
    /// <summary>身份：system（默认）/ user / assistant。决定消息以谁的名义发出。</summary>
    public string? Role { get; set; }
    public string? Content { get; set; }
    public bool Enabled { get; set; } = true;
    /// <summary>区域：system（默认，最靠前）/ features（功能说明）/ memory（记忆存档）/ chat（对话历史）。决定插在装配顺序的哪一段。</summary>
    public string? Group { get; set; }
    public int? Index { get; set; }
    public bool Constant { get; set; } = true;
    public bool Selective { get; set; }
    public bool CaseSensitive { get; set; }
    public List<string>? Keywords { get; set; }
    public List<string>? SecondaryKeywords { get; set; }
}

/// <summary>修改模块的入参：null = 不动这个字段。</summary>
public sealed class AgentModulePatch
{
    public string? Name { get; set; }
    /// <summary>身份：system / user / assistant。</summary>
    public string? Role { get; set; }
    public string? Content { get; set; }
    public bool? Enabled { get; set; }
    /// <summary>区域：system / features / memory / chat。</summary>
    public string? Group { get; set; }
    public int? Index { get; set; }
    public bool? Constant { get; set; }
    public bool? Selective { get; set; }
    public bool? CaseSensitive { get; set; }
    public List<string>? Keywords { get; set; }
    public List<string>? SecondaryKeywords { get; set; }
}

/// <summary>快照槽的一句话说明。</summary>
public sealed class AgentPresetBrief
{
    public string Name { get; set; } = "";
    public bool Auto { get; set; }
    public string UpdatedAt { get; set; } = "";
    public string Mode { get; set; } = "Off";
    public int Modules { get; set; }
    public int WorldbookEntries { get; set; }
    public bool HasTavernCard { get; set; }
}

/// <summary>操作结果：成功与否 + 数据 + 要给角色看的一句话。</summary>
public sealed class ContextAgentOpsResult<T>
{
    public bool Ok { get; init; }
    public T? Value { get; init; }
    public string Message { get; init; } = "";

    public static ContextAgentOpsResult<T> Succeed(T value) => new() { Ok = true, Value = value };
    public static ContextAgentOpsResult<T> Fail(string message) => new() { Ok = false, Message = message };

    /// <summary>
    /// 换上给人看的一句话。**失败时的 reason 不会被冲掉** —— 失败结果往往在
    /// <see cref="Fail"/> 里写了「为什么不行」，再 <c>With</c> 补一句上下文时
    /// 若直接覆盖，角色就只能看到补充信息、看不到真正的失败原因。
    /// 所以失败时改为「原因 + 补充」拼接，成功时才是替换。
    /// </summary>
    public ContextAgentOpsResult<T> With(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return this;
        var text = Ok || string.IsNullOrWhiteSpace(Message) ? message : Message + " " + message;
        return new() { Ok = Ok, Value = Value, Message = text };
    }
}
