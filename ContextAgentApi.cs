using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Alife.Function.FunctionCaller;
using Alife.Framework;

namespace Marisa.ContextManager;

/// <summary>
/// 角色侧接口：让角色**看**上下文、**改**上下文模块、管理自己的预设快照。
///
/// 这是本插件给 AI 的一面 —— 与给用户的那面（Electron 窗口）共用同一份计划和同一条落地路径。
///
/// <para><b>提示词分层</b>：这一组函数有 9 个，全塞进系统提示词会把上下文撑爆。
/// 所以走的是「先查目录、再动手」的两级暴露（和 <c>Marisa.Toolkit</c> 一个思路）：
/// 常驻提示词里只有一段**能力说明**（见 <see cref="BuildAgentPrompt"/>），
/// 告诉角色「你能干什么、要按什么规矩干」，真正的函数文档由 XmlFunctionCaller 自己注入。
/// 用户把两个开关都关掉时，这一整段提示词不会注入 —— 角色根本不知道有这回事。</para>
///
/// <para><b>三条规矩</b>（都写进提示词，也都在代码里强制）：
/// ① 看得到 ≠ 改得动，开关关着函数够不着；
/// ② 每一次改动都要用户点「批准」；
/// ③ 改动默认只落在「插件覆盖」上，不碰角色文件。</para>
/// </summary>
public sealed class ContextAgentApi
{
    readonly ContextManagerRuntime runtime;
    readonly Character character;
    readonly Interactor<ContextManagerModule> interactor;

    /// <param name="character">
    /// **本次注册属于哪个角色**。绝不能从 runtime 上取 —— 运行时是全局单例，
    /// 它的"当前角色"会被最后唤醒的那个角色覆盖（momo 与另一个角色同时启用时，
    /// 用 runtime 取角色会把 A 的操作算到 B 头上）。
    /// </param>
    /// <param name="interactor">该角色自己的交互器，用来把结果投回**它**的对话。</param>
    public ContextAgentApi(ContextManagerRuntime runtime, Character character, Interactor<ContextManagerModule> interactor)
    {
        this.runtime = runtime;
        this.character = character;
        this.interactor = interactor;
    }

    // ───────────────────────── 查看 ─────────────────────────

    [XmlFunction(FunctionMode.OneShot, order: 10)]
    [Description("查看某个角色的上下文装配计划：覆盖方式、模块总数、以及每个模块的编号 / 名称 / 身份 / 区域 / 是否参与发送 / 字数 / 正文开头。默认看自己；要看别的角色就填 owner。这个函数只读不写。")]
    public Task AgentContextList(
        [Description("要看谁的计划；留空 = 自己")] string owner = "",
        CancellationToken cancellationToken = default)
    {
        var target = ResolveTarget(owner);
        if (target == null) { Poke("要看哪个角色？请填上角色名，或留空看自己的。"); return Task.CompletedTask; }

        var result = runtime.AgentOps.Outline(target);
        if (!result.Ok || result.Value == null) { Poke(result.Message); return Task.CompletedTask; }

        var outline = result.Value;
        var sb = new StringBuilder();
        sb.AppendLine($"【{outline.Owner} 的上下文装配】覆盖方式：{outline.ModeLabel}；"
            + $"{outline.ModuleCount} 个模块（{outline.EnabledCount} 个参与发送），约 {outline.TotalChars} 字。");
        sb.AppendLine(outline.ApplyMacros ? "宏替换：开" : "宏替换：关");
        sb.AppendLine();
        if (outline.Modules.Count == 0)
            sb.AppendLine("（计划是空的。）");
        foreach (var module in outline.Modules)
            sb.AppendLine($"#{module.Index + 1} [{module.Id}] {module.Name}　"
                + $"{module.Role}/{module.Group}　{(module.Enabled ? "参与" : "停用")}　{module.Chars} 字　{module.Source}"
                + (module.Preview.Length > 0 ? "　「" + module.Preview + "」" : ""));
        sb.AppendLine();
        sb.Append("要看某个模块的完整正文，用「查看模块」并给出它的编号或 Id。");
        Poke(sb.ToString());
        return Task.CompletedTask;
    }

    [XmlFunction(FunctionMode.OneShot, order: 11)]
    [Description("读取某一个上下文模块的完整正文与全部配置（身份 / 区域 / 是否参与发送 / 关键词等）。用编号（id）或名称（name）指定模块。这个函数只读不写。")]
    public Task AgentContextRead(
        [Description("模块编号（「查看上下文」里 [ ] 中的那串）")] string id = "",
        [Description("模块名称；和 id 二选一即可")] string name = "",
        [Description("看哪个角色的模块；留空 = 自己")] string owner = "",
        CancellationToken cancellationToken = default)
    {
        var target = ResolveTarget(owner);
        if (target == null) { Poke("要看哪个角色的模块？请填上角色名，或留空看自己的。"); return Task.CompletedTask; }

        var result = runtime.AgentOps.ReadModule(target, id, name);
        if (!result.Ok || result.Value == null) { Poke(result.Message); return Task.CompletedTask; }

        var m = result.Value;
        var sb = new StringBuilder();
        sb.AppendLine($"【模块 {m.Name}】");
        sb.AppendLine($"Id：{m.Id}");
        sb.AppendLine($"身份：{m.Role}　区域：{m.Group}　来源：{m.Source}");
        sb.AppendLine($"参与发送：{(m.Enabled ? "是" : "否")}　常驻：{(m.Constant ? "是" : "否")}　选择性：{(m.Selective ? "是" : "否")}");
        if (m.Keywords.Count > 0) sb.AppendLine("关键词：" + string.Join("、", m.Keywords));
        if (m.SecondaryKeywords.Count > 0) sb.AppendLine("次要关键词：" + string.Join("、", m.SecondaryKeywords));
        sb.AppendLine();
        sb.AppendLine("正文：");
        sb.AppendLine(m.Content ?? "");
        Poke(sb.ToString());
        return Task.CompletedTask;
    }

    [XmlFunction(FunctionMode.OneShot, order: 12)]
    [Description("把当前装配计划按最终发送的样子渲染出来预览（含宏展开、按身份分块），用来确认改动是否符合预期。这个函数只读不写。")]
    public Task AgentContextPreview(
        [Description("看谁的装配；留空 = 自己")] string owner = "",
        CancellationToken cancellationToken = default)
    {
        var target = ResolveTarget(owner);
        if (target == null) { Poke("要预览哪个角色的装配？请填上角色名，或留空看自己的。"); return Task.CompletedTask; }

        var result = runtime.AgentOps.Preview(target, 8000);
        Poke(result.Ok ? result.Message + "\n\n" + result.Value : result.Message);
        return Task.CompletedTask;
    }

    // ─────────────────────── 模块增删改（受配置开关授权） ───────────────────────

    // ⚠️ 必须是 FunctionMode.Content，不能是 OneShot。
    // 「传正文」的标签在这个框架里只有一套正确写法，四个细节缺一不可：
    //   ① [XmlFunction(FunctionMode.Content)] —— 框架的调用模式校验是**位与**：
    //        (Mode & OneShot)==0 就用自闭合、 (Mode & Content)==0 就用包裹。
    //        写成 OneShot 只允许「单个自闭合标签」，而自闭合**根本传不进正文** ——
    //        一旦 AI 用包裹写法（唯一能带正文的方式）就会抛
    //        「调用 AgentContextAdd 标签的方式错误，应该使用单个自闭合标签调用」。
    //   ② 还要在 Mode==Content 时框架才会设 ContentName（XmlHandler.cs 那行三元），
    //        否则解析器不知道哪个参数该接正文。
    //   ③ 参数用 [XmlForm] 而不是 [XmlContent]：流式解析下 Closing 时 context.Content 是空的，
    //        正文累积在 FullContent（AboveContent + Content）里。[XmlForm] 会把整段正文
    //        当**原始文本**塞进参数，不做 XML 转义 —— 这正是「正文里有 < > &」时需要的行为。
    //   ④ 收尾判断 CallMode == Closing：Content 模式会先触发 Opening/Content 多次，
    //        只有 Closing 才是「正文收完了」，在别的时候干活会拿到半截正文。
    // 照抄的是框架自带 Alife.Function.FileService.Write/Edit 与 Marisa.AgentCollab.SubAgentSpawn。
    [XmlFunction(FunctionMode.Content, order: 20)]
    [Description("给某个角色的上下文计划新增一个模块。标签正文就是模块内容（纯文本，不用转义）。写入后还要再「应用」才会真正影响对话。默认加到自己身上。")]
    public async Task AgentContextAdd(
        XmlExecutorContext context,
        [Description("模块名称")] string name,
        [Description("身份（谁的名义发出）：system / user / assistant；留空按 system")] string role = "",
        [Description("区域（插在装配顺序哪一段）：system / features / memory / chat；留空跟随身份")] string group = "",
        [Description("加到第几位（从 1 开始）；留空 = 加到同区域末尾")] int index = 0,
        [Description("是否参与发送：true / false；留空 = true")] string enabled = "",
        [Description("加到哪个角色；留空 = 自己")] string owner = "",
        CancellationToken cancellationToken = default)
    {
        if (context.CallMode != CallMode.Closing) return;

        var target = ResolveTarget(owner);
        if (target == null) { Poke("要加到哪个角色？请填上角色名，或留空加到自己。"); return; }
        if (string.IsNullOrWhiteSpace(name)) { Poke("新增模块需要给一个 name（模块名称）。"); return; }

        var denied = runtime.CheckAgentAccess(new ContextManagerRuntime.AgentRequest(character.Name, target), "新增模块");
        if (denied != null) { Poke(denied); return; }

        var draft = new AgentModuleDraft
        {
            Name = name,
            Role = Blank(role) ? null : role,
            Content = context.FullContent,
            Group = Blank(group) ? null : group,
            Index = index > 0 ? index - 1 : null,
            Enabled = ParseBool(enabled, true)
        };
        var result = runtime.AgentOps.AddModule(target, draft);
        Poke(result.Message + (result.Ok ? "\n\n提示：改动还没生效，需要再调用「应用上下文」。" : ""));
    }

    // 同 AgentContextAdd：要传正文就必须是 FunctionMode.Content + [XmlForm] + FullContent + Closing。
    [XmlFunction(FunctionMode.Content, order: 21)]
    [Description("修改某个角色的上下文模块。只改你填了的字段，没填的保持不变。标签正文 = 新的模块内容（纯文本，不用转义）；不写正文就不改正文。写入后还要再「应用」才会影响对话。")]
    public async Task AgentContextUpdate(
        XmlExecutorContext context,
        [Description("要改的模块编号（或改用 name）")] string id = "",
        [Description("要改的模块名称（或改用 id）")] string name = "",
        [Description("新的模块名称；不改就留空")] string newName = "",
        [Description("新的身份（谁的名义发出）：system / user / assistant；不改就留空")] string role = "",
        [Description("新的区域（插在装配顺序哪一段）：system / features / memory / chat；不改就留空")] string group = "",
        [Description("是否参与发送：true / false；不改就留空")] string enabled = "",
        [Description("改哪个角色的模块；留空 = 自己")] string owner = "",
        CancellationToken cancellationToken = default)
    {
        if (context.CallMode != CallMode.Closing) return;
        var target = ResolveTarget(owner);
        if (target == null) { Poke("要改哪个角色的模块？请填上角色名，或留空改自己的。"); return; }
        if (Blank(id) && Blank(name)) { Poke("要改哪个模块？请给出 id（模块编号）或 name（模块名称）。"); return; }

        var denied = runtime.CheckAgentAccess(new ContextManagerRuntime.AgentRequest(character.Name, target), "修改模块");
        if (denied != null) { Poke(denied); return; }

        // 正文来自标签体：没写正文就不改正文（Blank 判断同时挡住「只写了空白」）。
        // ⚠️ 不能用 context.Content —— 流式解析下它在 Closing 时是空的，正文在 FullContent。
        var body = context.FullContent;
        var patch = new AgentModulePatch
        {
            Name = Blank(newName) ? null : newName,
            Role = Blank(role) ? null : role,
            Content = Blank(body) ? null : body,
            Group = Blank(group) ? null : group,
            Enabled = Blank(enabled) ? null : ParseBool(enabled, true)
        };
        var changed = new List<string>();
        if (patch.Name != null) changed.Add("名称→" + patch.Name);
        if (patch.Role != null) changed.Add("身份→" + patch.Role);
        if (patch.Group != null) changed.Add("区域→" + patch.Group);
        if (patch.Content != null) changed.Add($"正文（{patch.Content.Length} 字）");
        if (patch.Enabled.HasValue) changed.Add(patch.Enabled.Value ? "改为参与发送" : "改为停用");
        if (changed.Count == 0) { Poke("你没有填任何要修改的内容。可改：newName / role / group / 标签正文 / enabled。"); return; }


        var result = runtime.AgentOps.UpdateModule(target, Blank(id) ? null : id, Blank(name) ? null : name, patch);
        Poke(result.Message + (result.Ok ? "\n\n提示：改动还没生效，需要再调用「应用上下文」。" : ""));
    }

    [XmlFunction(FunctionMode.OneShot, order: 22)]
    [Description("删除某个角色的一个上下文模块。不可恢复。写入后还要再「应用」才会影响对话。")]
    public async Task AgentContextDelete(
        [Description("要删的模块编号（或改用 name）")] string id = "",
        [Description("要删的模块名称（或改用 id）")] string name = "",
        [Description("删哪个角色的模块；留空 = 自己")] string owner = "",
        CancellationToken cancellationToken = default)
    {
        var target = ResolveTarget(owner);
        if (target == null) { Poke("要删哪个角色的模块？请填上角色名，或留空删自己的。"); return; }
        if (Blank(id) && Blank(name)) { Poke("要删哪个模块？请给出 id（模块编号）或 name（模块名称）。"); return; }

        var denied = runtime.CheckAgentAccess(new ContextManagerRuntime.AgentRequest(character.Name, target), "删除模块");
        if (denied != null) { Poke(denied); return; }

        // 先把要删的东西读出来给用户看 —— 删之前必须让人知道删的是什么。
        var current = runtime.AgentOps.ReadModule(target, Blank(id) ? null : id, Blank(name) ? null : name);
        if (!current.Ok || current.Value == null) { Poke(current.Message); return; }


        var result = runtime.AgentOps.DeleteModule(target, Blank(id) ? null : id, Blank(name) ? null : name);
        Poke(result.Message + (result.Ok ? "\n\n提示：改动还没生效，需要再调用「应用上下文」。" : ""));
    }

    // ⚠️ 这个函数**故意**不带正文（FunctionMode.OneShot，自闭合标签），因为要导入的是
    // 一份 JSON —— 塞进标签正文就要整个 XML 转义一遍，转义错一个字整段就废。
    // 所以 JSON 走 `json` 属性原样传入（XmlHandler 只把声明过的参数按名绑定，
    // 多出来的属性留在 context.Parameters 里，这里自己取）。
    [XmlFunction(FunctionMode.OneShot, order: 23)]
    [Description("从角色卡 / 世界书 / 预设（酒馆格式的 JSON，或 .png 角色卡）解析出上下文模块并导入装配计划。用 path 给文件路径，或把 JSON 原样写在 json 属性里。导入是「加上去」、不覆盖已有内容，也不会改动角色文件；导入后还要再「应用」才会影响对话。")]
    public async Task AgentContextImport(
        XmlExecutorContext context,
        [Description("文件路径（支持 .json / .png 角色卡）")] string path = "",
        [Description("类型：card=角色卡 / worldbook=世界书 / preset=预设；留空 = 按内容自动识别")] string kind = "",
        [Description("导入给谁；留空 = 自己")] string owner = "",
        CancellationToken cancellationToken = default)
    {
        // OneShot 只会触发一次（CallMode == OneShot）；其它模式直接放过，避免重复导入。
        if (context.CallMode != CallMode.OneShot) return;

        var target = ResolveTarget(owner);
        if (target == null) { Poke("要导入给哪个角色？请填上角色名，或留空导入给自己。"); return; }

        // `json` 没有声明成参数（避免出现在函数文档里诱导 AI 手写长 JSON），
        // 但 AI 真写进来时也要认 —— 从原始属性表里取。
        var body = context.Parameters.TryGetValue("json", out var json) ? json
                 : context.Parameters.TryGetValue("text", out var text) ? text : "";
        if (Blank(path) && Blank(body))
        {
            Poke("要导入什么？请给出 path（文件路径），或把 JSON 原样写在 json 属性里。例如："
                + "\n  <agentcontextimport path=\"C:/…/角色卡.png\"/>"
                + "\n  <agentcontextimport kind=\"worldbook\" json=\"{&quot;entries&quot;:{…}}\"/>");
            return;
        }

        var denied = runtime.CheckAgentAccess(
            new ContextManagerRuntime.AgentRequest(character.Name, target), "导入角色卡 / 世界书 / 预设");
        if (denied != null) { Poke(denied); return; }

        var parsed = runtime.AgentOps.ParseImport(kind, body, path);
        if (!parsed.Ok || parsed.Value == null) { Poke(parsed.Message); return; }

        var result = runtime.AgentOps.ImportModules(target, parsed.Value);
        Poke(parsed.Message + "\n" + result.Message);
    }

    // ─────────────────────── 快照（受配置开关授权） ───────────────────────

    [XmlFunction(FunctionMode.OneShot, order: 30)]
    [Description("列出某个角色已有的预设快照（含自动快照）。只读。")]
    public Task AgentContextPresets(
        [Description("看谁的快照；留空 = 自己")] string owner = "",
        CancellationToken cancellationToken = default)
    {
        var target = ResolveTarget(owner);
        if (target == null) { Poke("要看哪个角色的快照？请填上角色名，或留空看自己的。"); return Task.CompletedTask; }

        var slots = runtime.AgentOps.ListPresets(target);
        var sb = new StringBuilder();
        sb.AppendLine($"【{target} 的预设快照】共 {slots.Count} 份。");
        foreach (var slot in slots)
            sb.AppendLine($"· {slot.Name}{(slot.Auto ? "（自动）" : "")}　"
                + $"{slot.Modules} 个模块　{ContextAgentOps.DescribeMode(slot.Mode)}　更新于 {slot.UpdatedAt}"
                + (slot.WorldbookEntries > 0 ? $"　含世界书 {slot.WorldbookEntries} 条" : ""));
        if (slots.Count == 0) sb.AppendLine("（还没有任何快照。）");
        sb.AppendLine();
        sb.Append("要保存 / 删除 / 切换快照，分别用「保存快照」「删除快照」「切换快照」。");
        Poke(sb.ToString());
        return Task.CompletedTask;
    }

    [XmlFunction(FunctionMode.OneShot, order: 31)]
    [Description("把某个角色**当前**的装配计划存成一份命名快照。")]
    public async Task AgentContextPresetSave(
        [Description("快照名称；留空或用角色名 = 覆盖自动快照")] string name = "",
        [Description("给谁存快照；留空 = 自己")] string owner = "",
        CancellationToken cancellationToken = default)
    {
        var target = ResolveTarget(owner);
        if (target == null) { Poke("要给哪个角色存快照？请填上角色名，或留空给自己存。"); return; }

        var denied = runtime.CheckAgentAccess(new ContextManagerRuntime.AgentRequest(character.Name, target), "保存快照");
        if (denied != null) { Poke(denied); return; }


        var result = runtime.AgentOps.SavePreset(target, name);
        Poke(result.Message);
    }

    [XmlFunction(FunctionMode.OneShot, order: 32)]
    [Description("删除某个角色的一份命名快照（自动快照不可删）。")]
    public async Task AgentContextPresetDelete(
        [Description("要删的快照名称")] string name,
        [Description("删谁的快照；留空 = 自己")] string owner = "",
        CancellationToken cancellationToken = default)
    {
        var target = ResolveTarget(owner);
        if (target == null) { Poke("要删哪个角色的快照？请填上角色名，或留空删自己的。"); return; }
        if (Blank(name)) { Poke("要删哪一份快照？请给出 name（快照名称）。"); return; }

        var denied = runtime.CheckAgentAccess(new ContextManagerRuntime.AgentRequest(character.Name, target), "删除快照");
        if (denied != null) { Poke(denied); return; }


        var result = runtime.AgentOps.DeletePreset(target, name);
        Poke(result.Message);
    }

    [XmlFunction(FunctionMode.OneShot, order: 33)]
    [Description("切换（装载）某个角色的一份快照：把它里面的计划装回当前计划。装载后还要再「应用」才会影响对话。")]
    public async Task AgentContextPresetLoad(
        [Description("快照名称")] string name,
        [Description("切谁的快照；留空 = 自己")] string owner = "",
        CancellationToken cancellationToken = default)
    {
        var target = ResolveTarget(owner);
        if (target == null) { Poke("要切哪个角色的快照？请填上角色名，或留空切自己的。"); return; }
        if (Blank(name)) { Poke("要切哪一份快照？请给出 name（快照名称）。"); return; }

        var denied = runtime.CheckAgentAccess(new ContextManagerRuntime.AgentRequest(character.Name, target), "切换快照");
        if (denied != null) { Poke(denied); return; }


        var result = runtime.AgentOps.LoadPreset(target, name);
        Poke(result.Message);
    }

    // ─────────────────────── 应用 ───────────────────────

    [XmlFunction(FunctionMode.OneShot, order: 40)]
    [Description("把当前磁盘上的装配计划**应用到真实对话**。改完模块之后必须调用它生效。改自己一律用插件覆盖（不动角色文件）。")]
    public async Task AgentContextApply(
        [Description("应用到哪个角色；留空 = 自己")] string owner = "",
        CancellationToken cancellationToken = default)
    {
        var target = ResolveTarget(owner);
        if (target == null) { Poke("要应用到哪个角色？请填上角色名，或留空应用到自己。"); return; }

        var denied = runtime.CheckAgentAccess(new ContextManagerRuntime.AgentRequest(character.Name, target), "应用上下文");
        if (denied != null) { Poke(denied); return; }

        var (ok, message) = await runtime.ApplyPlanForAgentAsync(new ContextManagerRuntime.AgentRequest(character.Name, target), false);
        Poke(message);
    }

    // ─────────────────────── 提示词 ───────────────────────

    /// <summary>
    /// 注入给角色的能力说明。只在「至少开了一个开关」时注入 ——
    /// 两个都关就等于这个插件对 AI 不存在（它连自己能不能改都不知道）。
    /// </summary>
    /// <param name="handler">
    /// 本模块注册的那份函数表。<b>函数文档必须从这里取</b>，不能手写 ——
    /// 见下面 <c>## 函数</c> 一段的注释。
    /// </param>
    public string BuildPrompt(XmlHandler? handler)
    {
        // ⚠️ 必须查「本模块所属角色自己」的配置，不能用单例上共享的 `Config`。
        // 每个角色都装了本插件时，别的角色每帧会把 `Config` 覆盖成自己的 ——
        // 于是本角色的能力说明（乃至「有没有能力」）会跟着别人变（2026-10-01 同一处根因）。
        var own = runtime.ConfigFor(character.Name);
        var self = own.AllowModifySelf;
        var others = own.AllowModifyOthers;
        if (!self && !others) return "";

        var sb = new StringBuilder();
        sb.AppendLine("「上下文管理器」管着你每次发言时真正发给模型的内容。你可以查看和修改它。");
        if (others)
            sb.AppendLine("改别人的上下文要写 owner（角色名）——那是干预别人，只在有正当理由时做。");
        sb.AppendLine();

        // ── 提供函数 ──────────────────────────────────────────────────────
        // ⚠️ 这一段不能省，也不能手写。
        //
        // 本模块用 RegisterHandlerWithoutDocument 注册（DocumentMode.None），
        // 而 XmlFunctionCaller.UpdatePrompt() 只拼接 explicitHandlers / implicitHandlers ——
        // None 两边都不进，**框架永远不会把这 11 个函数写进提示词**。
        // 结果：AI 只知道「能查看、能修改」这种口语化描述，却不知道函数叫什么、
        // 参数叫什么、哪些可选、正文该放在标签里还是属性里 —— 只能靠猜。
        //
        // 工具箱（Marisa.Toolkit）在同一个位置上，做的是把 handler.FunctionDocument()
        // 直接拼进提示词。这里照抄那个做法：文档由框架从 [XmlFunction] + [Description]
        // + 参数签名自动渲染，功能和参数一改，文档自动跟着变，不存在手写不同步的问题。
        sb.AppendLine("## 函数");
        sb.AppendLine(handler?.FunctionDocument() ?? "（函数说明尚未就绪）");
        sb.AppendLine();

        // ⚠️ 这里只讲**文档生成不出来的东西**：调用形式、正文怎么给、改动何时生效。
        // 参数名 / 类型 / 可选性都在上面的函数文档里，不要重复 —— 提示词越长，AI 越容易漏读关键约束。
        sb.AppendLine("## 要点");
        sb.AppendLine("· 两种形式，用错会报「标签的方式错误」：");
        sb.AppendLine("  - 自闭合 `<agentcontextlist/>`：除 add / update 外的全部函数都这样写。");
        sb.AppendLine("  - 开闭包裹 `<agentcontextadd name=\"…\">正文</agentcontextadd>`：只有 add / update；正文是模块内容。");
        sb.AppendLine("· add / update 的正文是纯文本、原样写入，`<` `>` `&` 不用转义，换行与缩进保留；update 不写正文就只改参数、不动正文。");
        sb.AppendLine("· 编号取 list 输出里 `[ ]` 中的那串；可以一次连写多个标签，结果合并成一条回给你。");
        sb.AppendLine();
        sb.AppendLine("## 导入");
        sb.AppendLine("`agentcontextimport` 能吃酒馆角色卡（.png / .json）、世界书、预设。"
            + "`path=\"文件路径\"`，或把 JSON 原样放属性里 `json=\"{…}\"`；kind 留空按内容自动认。"
            + "导入是**加上去**：同名自动跳过、已有模块保留，也不会写回角色目录。");
        sb.AppendLine();
		sb.AppendLine("## 人格快照玩法");
        sb.AppendLine("一份快照（=预设）= 一套完整人格（性格、口癖、记忆、设定整体切换），可以当「人格槽」玩多重人格。");
        sb.AppendLine("· 用户说「换个人格 / 换个预设 / 切到 XX」：先「查看快照」。有 → 装载 + 「应用上下文」，然后以新人格开口打招呼；没有 → 先改模块、应用、再「保存快照」存成新人格。起名要一眼能认。");
        sb.AppendLine("· 切换是整体替换，当前未保存的改动会丢 —— 想保留就先存一份再走。");
        sb.AppendLine("· 与角色同名的自动快照是「出厂人格」，玩乱了切回去即可；人格快照删除 = 杀掉一个人格，动手前必须问用户。");
        sb.AppendLine();
        sb.AppendLine("## 规矩");
        sb.AppendLine("1. 查看随时可做；修改由用户的权限开关授权，开着就不必反复确认。改完必须再调一次 `agentcontextapply` 才影响对话。");
        sb.AppendLine("2. 改动只重排请求内容，不改角色文件（导入也一样），关掉插件即复原。");
        if (others)
            sb.AppendLine("3. 改别人要写 owner；对方没启用插件时用户会被问一次 —— 不同意就不要再试。");
        sb.AppendLine($"{(( others) ? 4 : 3)}. 别为了「优化自己」删角色设定：改上下文等于改人格。");
        return sb.ToString().Trim();
    }

    // ─────────────────────── 工具 ───────────────────────

    /// <summary>把 owner 参数解析为真实目标角色名；留空 = 自己；角色不存在 = null。</summary>
    string? ResolveTarget(string owner)
    {
        if (Blank(owner)) return character.Name;
        var trimmed = owner.Trim();
        return runtime.GetCharacters().FirstOrDefault(c => string.Equals(c.Name, trimmed, StringComparison.OrdinalIgnoreCase))?.Name
            ?? (string.Equals(trimmed, character.Name, StringComparison.OrdinalIgnoreCase) ? character.Name : null);
    }

    static bool Blank(string? value) => string.IsNullOrWhiteSpace(value);

    static bool ParseBool(string? value, bool fallback)
    {
        if (Blank(value)) return fallback;
        var v = value!.Trim().ToLowerInvariant();
        return v switch
        {
            "true" or "1" or "yes" or "on" or "是" or "开" or "启用" => true,
            "false" or "0" or "no" or "off" or "否" or "关" or "停用" => false,
            _ => fallback
        };
    }

    void Poke(string message)
    {
        try { interactor.Poke(message); }
        catch (System.Exception ex) { ContextTrace.Write("agent poke failed: " + ex.Message); }
    }
}
