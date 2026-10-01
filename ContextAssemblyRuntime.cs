using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Alife.Framework;
using ElectronNET.API;
using ElectronNET.API.Entities;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Marisa.ContextManager;
public sealed partial class ContextManagerRuntime
{
    // 注意：这里以前有一个 `int references;`，全项目只声明、从未读写 —— 已删除。
    //
    // 【第三十二轮：把「请求前重排」从 ContextTransform 反射改成官方事件订阅】
    // 老做法是反射去挂 LanguageModel 上一个叫 ContextTransform 的私有属性 —— 那是别人给
    // OpenAI 插件硬加的口子，不属于 Alife 官方 API（官方源码全库搜 ContextTransform 零命中，
    // ILanguageModel 只有 ChatStreamingAsync 一个方法，没有任何请求前钩子）。
    // 强依赖它的后果：一旦这个私有口子没了（插件回退/换模型），请求前重排整条链路直接死掉。
    //
    // 现在改用官方 ChatBot.ChatSent 事件：ChatBot.ChatAsync 的时序是
    //   127  EditChatHistoryAsync(装载用户消息)   ← 用户消息进历史
    //   136  ChatSent?.Invoke(...)                ← 我们在这里
    //   149  EditChatHistoryAsync(真请求)         ← 语言模型读到的是我们重排后的历史
    // 且 ChatSent 触发时 chatHistorySemaphore 已被第 127 行那次编辑释放，
    // 所以在回调里同步调 EditChatHistory 不会死锁。
    //
    // 键从「LanguageModel 对象」换成「ChatBot 对象」：事件挂在 ChatBot 上，
    // 而 LanguageModel 可能在运行时被换（重载插件），挂错对象就会漏摘/漏挂。
    readonly Dictionary<ChatBot, Action<string>> chatSentSubscriptions = new();
    readonly object chatSentGate = new();
    JObject ReadIndex(string owner) {
        var character = GetCharacterRequired(owner);
        var dir = ResolveCharacterDirectory(character, FindStorageRoot()) ?? throw new InvalidOperationException("角色目录不存在");
        var index=JObject.Parse(File.ReadAllText(Path.Combine(dir,"index.json")));
        var assemblyPath=Path.Combine(dir,"ContextAssembly.json");
        if(File.Exists(assemblyPath)) foreach(var field in JObject.Parse(File.ReadAllText(assemblyPath)).Properties()) index[field.Name]=field.Value.DeepClone();
        var cardPath=Path.Combine(dir,"TavernCard.json");
        if(File.Exists(cardPath)) index["TavernCard"]=JObject.Parse(File.ReadAllText(cardPath));
        return index;
    }
    string TempPlanPath(string owner) => Path.Combine(GetOverrideDirectory(owner), "applied.json");
    string PlanDirectory()
    {
        // 计划数据与预设一样放在 Storage 根下，避免插件目录被重新编译/覆盖部署时丢失，
        // 也避免依赖插件目录的可写性。
        try
        {
            var root = FindStorageRoot();
            if (!string.IsNullOrWhiteSpace(root))
                return Path.Combine(root, "ContextManager", "Plans");
        }
        catch (Exception ex) { ContextTrace.Write("plan dir fallback: " + ex.Message); }
        return Path.Combine(pluginSystem.PluginContext.GetPluginDirectoryPath("Marisa.ContextManager"), "Plans");
    }

    ContextPlanService GetPlanService() => planService ??= CreatePlanService();

    ContextPlanService CreatePlanService()
    {
        var dir = PlanDirectory();
        Directory.CreateDirectory(dir);
        ContextTrace.Write("plan service directory=" + dir);
        // 新建计划时用**完整官方系统消息**填充「角色设定」模块（名称/生日/简介/设定/私人文件夹），
        // 这样插件覆盖下系统消息整段都可编辑。
        return new ContextPlanService(dir, ReadIndex, (index, owner) =>
        {
            try
            {
                var character = index?.ToObject<Character>() ?? GetCharacterRequired(owner);
                return BuildOfficialSystemPrompt(character, FindStorageRoot());
            }
            catch (Exception ex)
            {
                ContextTrace.Write("official prompt build failed owner=" + owner + " : " + ex.Message);
                return "";
            }
        });
    }

    // 解析“当前应当生效”的装配计划：
    //   1) applied.json —— 应用/自动侧装时写下的插件覆盖快照；
    //   2) 已保存的计划本身 —— 只要它是「插件覆盖」就自动生效，用户不必再手动点“应用到当前角色”；
    //   3) 角色 index.json 里遗留的 ContextManagerPlan（旧数据）。
    ContextPlan? ResolveActivePlan(string owner, JObject index)
    {
        try
        {
            var path = TempPlanPath(owner);
            if (File.Exists(path))
            {
                var applied = JsonConvert.DeserializeObject<ContextPlan>(File.ReadAllText(path));
                if (applied != null && applied.Mode == "Temporary") return applied;
            }
        }
        catch (Exception ex) { ContextTrace.Write("resolve plan(applied) failed owner=" + owner + " : " + ex.Message); }
        try
        {
            var saved = GetPlanService().GetOrCreatePlan(owner);
            if (saved != null && saved.Mode == "Temporary") return saved;
        }
        catch (Exception ex) { ContextTrace.Write("resolve plan(saved) failed owner=" + owner + " : " + ex.Message); }
        try { return index["ContextManagerPlan"]?.ToObject<ContextPlan>(); }
        catch { return null; }
    }

    /// <summary>
    /// 给一个会话挂上「请求前重排」：订阅官方 ChatBot.ChatSent 事件。
    /// 返回是否处于挂载态（已挂 或 本次挂上）。
    /// </summary>
    bool AttachTransform(ChatActivity activity) {
        var bot = activity?.ChatBot;
        if (bot == null) return false;
        lock (chatSentGate) {
            if (chatSentSubscriptions.ContainsKey(bot)) return true;
            // 闭包里只捕获 bot 与 owner（值类型字符串），不捕获 activity，避免旧运行时被整条
            // 会话链闭包引用着无法回收。
            var owner = activity!.Character?.Name ?? "";
            if (string.IsNullOrWhiteSpace(owner)) return false;
            Action<string> handler = _ => ReorderBeforeSend(bot, owner);
            bot.ChatSent += handler;
            chatSentSubscriptions[bot] = handler;
            return true;
        }
    }

    /// <summary>
    /// 请求前重排：ChatBot.ChatSent 触发时（用户消息已装载、语言模型请求未发出）就地改写会话历史。
    /// 这段代码运行在**每一次**请求前，抛异常会把原生对话窗口的整轮对话打断，
    /// 因此全程兜底：任何计划/文件/宏问题都只降级为「按原文发送」，绝不外抛。
    /// </summary>
    void ReorderBeforeSend(ChatBot bot, string owner) {
        try {
            var index = ReadIndex(owner);
            var plan = ResolveActivePlan(owner, index);
            if (plan == null || plan.Mode != "Temporary") return;
            // bot.ChatHistory 是对外快照（IReadOnlyList），Compiler 要 ChatHistory —— 按序拷一份。
            // 这一步必须在 EditChatHistory 之外做（快照语义），避免在持有 chatHistorySemaphore 时重入读取。
            var source = new ChatHistory();
            foreach (var message in bot.ChatHistory) source.Add(message);
            var warnings = new List<string>();
            var compiled = ContextCompiler.Compile(source, plan, index, owner, warnings);
            foreach (var warning in warnings)
                ContextTrace.Write("reorder warn owner=" + owner + " : " + warning);
            // ChatSent 触发时第 127 行那次 EditChatHistory 已释放 chatHistorySemaphore，
            // 这里同步调 EditChatHistory 不会死锁。用「整体替换」语义：清空后按编译结果重填。
            bot.EditChatHistory(thread => {
                thread.ChatHistory.Clear();
                foreach (var message in compiled) thread.ChatHistory.Add(message);
            }, "ContextManager 请求前重排");
        } catch (Exception ex) {
            ContextTrace.Write("reorder FAILED owner=" + owner + " : " + ex.GetType().Name + ": " + ex.Message + "（已按原文发送，未打断对话）");
        }
    }

    // 自动侧装：只要该角色保存的计划是「插件覆盖」，插件一启动（或角色一激活）就自动
    // 挂上请求前的上下文重组，并把计划写成 applied.json。用户不需要再打开窗口点
    // “应用到当前角色”。「本地覆盖」不需要侧装（index.json 的 Prompt 已经是最终内容）。
    internal bool TryAutoAttach(ChatActivity activity)
    {
        try
        {
            var owner = activity?.Character?.Name;
            if (string.IsNullOrWhiteSpace(owner)) return false;
            // 计划文件丢失时先用自动角色预设恢复，保证“插件一启用就能直接用”。
            TryRestoreCharacterPreset(owner);
            var plan = GetPlanService().GetOrCreatePlan(owner);
            if (plan == null) return false;
            if (plan.Mode != "Temporary")
            {
                ContextTrace.Write($"auto attach skip owner={owner} mode={plan.Mode}");
                return false;
            }
            AtomicWrite(TempPlanPath(owner), JsonConvert.SerializeObject(plan, Formatting.Indented));
            var attached = AttachTransform(activity);
            ContextTrace.Write($"auto attach owner={owner} mode={plan.Mode} modules={plan.Modules.Count} attached={attached}");
            return attached;
        }
        catch (Exception ex)
        {
            ContextTrace.Write("auto attach FAILED owner=" + activity?.Character?.Name + " : " + ex.GetType().Name + ": " + ex.Message);
            return false;
        }
    }

    internal void TryAutoAttachAll()
    {
        try
        {
            foreach (var activity in chatActivitySystem.GetAllChatActivities()) TryAutoAttach(activity);
        }
        catch (Exception ex) { ContextTrace.Write("auto attach all failed: " + ex.Message); }
    }
    void DetachTransform(ChatBot? bot) {
        if (bot == null) return;
        lock (chatSentGate) {
            if (chatSentSubscriptions.Remove(bot, out var handler)) bot.ChatSent -= handler;
        }
    }
    void OnActivityDeactivated(ChatActivity activity) => DetachTransform(activity?.ChatBot);
    /// <summary>
    /// 摘掉所有挂在 ChatBot.ChatSent 上的委托。由 Release()（插件卸载）调用，
    /// 以及角色停用时按会话逐个摘（见 OnActivityDeactivated）。
    /// 不做的话：插件重载后旧订阅还挂在活着的 ChatBot 上，同一个会话会被重排两次，
    /// 且旧运行时被闭包引用着无法回收。
    /// </summary>
    void DetachTransforms() {
        lock (chatSentGate) {
            foreach (var pair in chatSentSubscriptions) pair.Key.ChatSent -= pair.Value;
            chatSentSubscriptions.Clear();
        }
    }
    void PlanOperation(string owner, Action action) {
        try { lock (stateLock) { GetCharacterRequired(owner); action(); } }
        catch (Exception ex) {
            logger.LogError(ex,"上下文计划操作失败");
            ContextTrace.Write("plan operation FAILED owner=" + owner + " : " + ex.GetType().Name + ": " + ex.Message);
            SendWindow("plan-error",new { owner, message=ex.Message });
        }
    }
    ContextPlan ParsePlan(JsonElement payload) {
        if (!payload.TryGetProperty("plan",out var value)) throw new InvalidOperationException("缺少计划");
        var plan = JsonConvert.DeserializeObject<ContextPlan>(value.GetRawText()) ?? throw new InvalidOperationException("无效计划");
        ContextCompiler.Validate(plan); return plan;
    }
    /// <summary>
    /// 把「只下发了预览」的模块正文按 Id 从磁盘上的计划里回填。
    ///
    /// 装配页收到的计划可能是瘦身版（模块正文被截成预览、Truncated=true）—— 那是为了让
    /// 142 个模块的计划塞得进一条 IPC 报文。渲染进程把这份计划原样发回来时，如果不回填，
    /// save / preview / apply 就会拿预览当正文，等于把用户的世界书正文毁掉。
    /// **这是防丢数据的关键一步，必须在所有写入路径之前执行。**
    ///
    /// 回填不到就抛异常中止，而不是"凑合继续"：宁可这次操作失败，也不能把预览落盘。
    /// </summary>
    void RestoreTruncatedModules(string owner, ContextPlan plan) {
        if (!plan.Modules.Any(m => m.Truncated)) return;
        ContextPlan? stored = null;
        try { stored = GetPlanService().GetOrCreatePlan(owner); }
        catch (Exception ex) { ContextTrace.Write($"plan restore: read stored failed owner={owner}: {ex.Message}"); }
        var byId = new Dictionary<string, ContextPlanModule>(StringComparer.Ordinal);
        if (stored?.Modules != null)
            foreach (var module in stored.Modules)
                if (!string.IsNullOrEmpty(module.Id)) byId[module.Id] = module;
        var restored = 0; var missing = 0;
        foreach (var module in plan.Modules) {
            if (!module.Truncated) continue;
            if (byId.TryGetValue(module.Id, out var origin)) { module.Content = origin.Content; restored++; }
            else missing++;
            module.Truncated = false;   // 无论成败都清标记：它绝不能落盘
        }
        ContextTrace.Write($"plan restore owner={owner} restored={restored} missing={missing}");
        if (missing > 0)
            throw new InvalidOperationException(
                $"有 {missing} 个模块的正文没能从磁盘上的计划里取回（通常是刚新增、还没保存过的模块）。" +
                "为避免把预览当成正文写回去，这次操作已中止。请点开这些模块读取全文后再试。");
    }

    ContextPlan PreparePlan(string owner, JsonElement payload) {
        var plan = ParsePlan(payload);
        RestoreTruncatedModules(owner, plan);
        // 渲染端在计划回复到达前会先用本地草稿兜底；那种草稿可能完全没有「角色设定」模块。
        // 只在计划里**一个 native 模块都没有**时补一个官方系统消息模块。
        // 不能写成“只要 native 内容为空就回填”：用户在装配页主动「重置为空白」，
        // 或导入角色卡（会移除 native 模块）之后，每次应用又被塞回来，等于抹掉用户的整理结果。
        var character = ReadActiveCharacterFromDisk(GetCharacterRequired(owner), FindStorageRoot());
        if (!plan.Modules.Any(m => m.Source == "native"))
            plan.Modules.Insert(0, new ContextPlanModule {
                Name = "角色设定 #0", Source = "native", Group = "system", Role = "system",
                Content = BuildOfficialSystemPrompt(character, FindStorageRoot()), Enabled = true
            });
        UpgradeNativeModuleContent(plan, character, FindStorageRoot());
        // Runtime cards contain bounded previews. Refresh the exact source text
        // before applying a selected module so long messages pass validation.
        var history = FindActivity(owner)?.ChatBot.ChatHistory;
        if (history != null)
        {
            ContextPromptText.MigrateFrameworkModules(plan, history);
            var resolved = ContextPromptText.ResolveFrameworkIndices(plan.Modules, history);
            foreach (var pair in resolved)
            {
                var original = history[pair.Value];
                pair.Key.OriginalContent = original.Content ?? "";
                pair.Key.OriginalRole = original.Role.Label;
            }
            // Unresolved overrides are retained for recovery; never rebind them by position.
        }
        return plan;
    }

    // UI operations and request compilation share the same one-to-one identity resolver.
    internal static int ResolveFrameworkIndex(ContextPlanModule module, IReadOnlyList<ChatMessageContent> history)
        => ResolveFrameworkIndex(module, history, null);

    internal static int ResolveFrameworkIndex(ContextPlanModule module, IReadOnlyList<ChatMessageContent> history,
        IReadOnlyList<ContextPlanModule>? plan)
    {
        var resolved = ContextPromptText.ResolveFrameworkIndices(plan ?? new[] { module }, history);
        return resolved.TryGetValue(module, out var at) ? at : -1;
    }
    static void FillNativePlanPrompt(ContextPlan plan, Character character, string? storageRoot)
    {
        // 只在计划完全为空（首次创建，或计划文件损坏被重建）时补一个官方模块。
        // 不能写成“只要 native 为空就回填”：用户在装配页主动「重置为空白」，或导入角色卡
        // （会移除 native 模块）之后，每次打开窗口又被塞回来，等于把用户的整理结果抹掉。
        if (plan.Modules.Count == 0)
            plan.Modules.Insert(0, new ContextPlanModule { Name = "角色设定 #0", Source = "native", Group = "system", Role = "system", Content = BuildOfficialSystemPrompt(character, storageRoot), Enabled = true });
        UpgradeNativeModuleContent(plan, character, storageRoot);
    }
    // 旧计划里的「角色设定」模块只存了 index.json 的裸 Prompt。现在统一升级为**完整官方系统
    // 消息**，这样插件覆盖下用户能把框架注入的「名称/生日/简介/设定/私人文件夹」一起改。
    // 判定规则见 ContextPromptText.UpgradeNativeModules（可单元测试）。
    static void UpgradeNativeModuleContent(ContextPlan plan, Character character, string? storageRoot)
    {
        var upgraded = ContextPromptText.UpgradeNativeModules(plan.Modules, character?.Prompt,
            BuildOfficialSystemPrompt(character!, storageRoot));
        if (upgraded > 0)
            ContextTrace.Write($"native modules upgraded to official system prompt: {upgraded}");
    }
    internal const string OfficialPromptMarker = ContextPromptText.OfficialPromptMarker;
    // 统一收集未识别的宏，供界面提示（不再中断应用/预览）。
    static List<string> CollectUnresolvedMacros(ContextPlan plan, JObject index, string owner)
    {
        var unresolved = new List<string>();
        if (!plan.ApplyMacros) return unresolved;
        try
        {
            var env = ContextCompiler.Environment(index, owner, plan.UserName, plan.CustomMacros);
            var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var module in plan.Modules.Where(m => m.Enabled))
                ContextCompiler.Render(module.Content, env, vars, unresolved, plan.AutoMacros);
        }
        catch (Exception ex) { ContextTrace.Write("macro scan failed: " + ex.Message); }
        return unresolved;
    }
    string RenderLocalOverride(string owner, ContextPlan plan, JObject index)
    {
        var env = ContextCompiler.Environment(index, owner, plan.UserName, plan.CustomMacros);
        var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var blocks = new List<string>();
        foreach (var group in ContextCompiler.Groups)
        foreach (var module in plan.Modules.Where(m => m.Enabled && m.Group == group && m.Source != "framework"))
        {
            var text = plan.ApplyMacros ? ContextCompiler.Render(module.Content, env, vars, null, plan.AutoMacros) : module.Content;
            if (string.IsNullOrWhiteSpace(text)) continue;
            // 「角色设定」模块保存的是完整官方系统消息。本地覆盖要把它写回 index.json 的
            // Prompt，而框架会再套一层同样的外壳（名称/生日/简介/私人文件夹），
            // 所以这里必须先剥掉外壳只写“设定”正文，否则会出现两份人物信息。
            if (module.Source == "native") text = ExtractPromptBody(text);
            if (string.IsNullOrWhiteSpace(text)) continue;
            blocks.Add($"【{ContextCompiler.Role(module.Role)}】\n{text.Trim()}");
        }
        return string.Join("\n\n", blocks).Trim();
    }
    // 计划报文的预算（转义后字节）。超了就**瘦身下发**，不再拒发。
    //
    // 2026-09-30 的教训：一张 142 条世界书的角色卡，导入后计划有 143 个模块、正文 9.9 万字，
    // 序列化后必然超过这个预算。旧逻辑直接 SendWindow("plan-error") 拒发，而调用方紧接着
    // 又发了 plan-imported（「导入完成」）—— 后到的弹窗把先到的错误顶掉，
    // 用户看到的就是「提示导入成功，但装配页一片空白」。
    // 现在的做法：模块正文按剩余预算逐级缩水并打 Truncated，让装配页先把结构渲染出来；
    // 前端点开某个模块时再走 plan:module 取全文，写回时由 PreparePlan 按 Id 从磁盘回填。
    const int PlanWireBudgetBytes = 448 * 1024;

    /// <summary>下发计划。返回被瘦身（正文只发了预览）的模块数，调用方可以据此提示用户。</summary>
    int SendPlan(string owner, ContextPlan plan)
    {
        var history = FindActivity(owner)?.ChatBot.ChatHistory;
        if (history != null) ContextPromptText.MigrateFrameworkModules(plan, history);
        ContextPromptText.NormalizeFrameworkNames(plan.Modules);
        JObject wire;
        long fullBytes;
        try
        {
            wire = JObject.FromObject(plan, Newtonsoft.Json.JsonSerializer.Create(ContextJsonSettings.Wire));
            fullBytes = ContextStateBudget.EscapedBytes(wire.ToString(Formatting.None));
        }
        catch (Exception ex)
        {
            ContextTrace.Write($"plan wire measure failed owner={owner}: {ex.Message}");
            SendWindow("plan-state", new { owner, plan });
            return 0;
        }
        var truncatedModules = fullBytes > PlanWireBudgetBytes ? ShrinkPlanForWire(wire, fullBytes, PlanWireBudgetBytes) : 0;
        if (truncatedModules > 0)
            ContextTrace.Write($"plan shrunk owner={owner} modules={plan.Modules.Count} truncated={truncatedModules} full={fullBytes}B limit={PlanWireBudgetBytes}B");
        SendWindow("plan-state", new { owner, plan = wire, truncatedModules, fullBytes });
        return truncatedModules;
    }

    /// <summary>
    /// 把计划压进预算。实现在 <see cref="ContextStateBudget.ShrinkPlanForWire"/> ——
    /// state / character-bundle / plan-state / worldbook-state 四条路径共用同一套
    /// 「放得下给全文、放不下给预览」的规则，各写一份必然不一致；而且那边是纯函数，可单元测试。
    /// </summary>
    static int ShrinkPlanForWire(JObject wire, long fullBytes, int budgetBytes)
        => ContextStateBudget.ShrinkPlanForWire(wire, fullBytes, budgetBytes);

    /// <summary>
    /// 下发「**外部改动**」的计划：报文类型是 <c>plan-updated</c> 而不是 <c>plan-state</c>。
    ///
    /// <para><b>为什么要单独一个类型：</b>两者的语义完全不同 ——</para>
    /// <list type="bullet">
    /// <item><c>plan-state</c> = 「你请求的那份计划回来了」。前端处理时**遇到 <c>planDirty</c>
    /// 就整个丢弃**（<c>if (planDirty) return;</c>），因为那通常只是自己刚发出去的请求的回声，
    /// 覆盖会打乱正在编辑的草稿。</item>
    /// <item><c>plan-updated</c> = 「**别人**（角色 / AI）把计划改了」。这条**不能被静默丢弃** ——
    /// 丢了就正是用户报的那个 bug：AI 明明加了模块，界面上却什么都没有，必须关掉重开才看得到。</item>
    /// </list>
    ///
    /// <para>前端对 <c>plan-updated</c> 的约定：没在编辑就直接换上新计划；正在编辑就**保留草稿**
    /// 并弹一条「AI 改过，点这里看最新的」提示条（见 app.js 的 <c>plan-updated</c> 分支）。</para>
    ///
    /// <para>瘦身逻辑与 <see cref="SendPlan"/> 完全一致（共用 <c>ShrinkPlanForWire</c>），
    /// 否则一条大计划会把这个新报文变成顶断 IPC 桥的入口。</para>
    /// </summary>
    void SendPlanUpdated(string owner, ContextPlan plan)
    {
        var history = FindActivity(owner)?.ChatBot.ChatHistory;
        if (history != null) ContextPromptText.MigrateFrameworkModules(plan, history);
        ContextPromptText.NormalizeFrameworkNames(plan.Modules);
        JObject wire;
        long fullBytes;
        try
        {
            wire = JObject.FromObject(plan, Newtonsoft.Json.JsonSerializer.Create(ContextJsonSettings.Wire));
            fullBytes = ContextStateBudget.EscapedBytes(wire.ToString(Formatting.None));
        }
        catch (Exception ex)
        {
            ContextTrace.Write($"plan-updated wire measure failed owner={owner}: {ex.Message}");
            SendWindow("plan-updated", new { owner, plan });
            return;
        }
        var truncatedModules = fullBytes > PlanWireBudgetBytes ? ShrinkPlanForWire(wire, fullBytes, PlanWireBudgetBytes) : 0;
        ContextTrace.Write($"plan-updated owner={owner} modules={plan.Modules.Count} truncated={truncatedModules} full={fullBytes}B");
        SendWindow("plan-updated", new { owner, plan = wire, truncatedModules, fullBytes });
    }

    /// <summary>取单个模块的完整正文（计划被瘦身下发后，装配页按需读取）。</summary>
    void GetPlanModule(string owner, JsonElement payload)
    {
        var id = JsonString(payload, "id") ?? "";
        try
        {
            var plan = GetPlanService().GetOrCreatePlan(owner);
            var module = plan.Modules.FirstOrDefault(m => m.Id == id);
            if (module == null)
            {
                SendWindow("plan-module", new { owner, id, error = "找不到这个模块（可能已被删除）：" + id });
                return;
            }
            var content = module.Content ?? "";
            SendWindow("plan-module", new { owner, id, name = module.Name, content, contentLength = content.Length });
        }
        catch (Exception ex)
        {
            ContextTrace.Write($"plan module load failed owner={owner} id={id}: {ex.Message}");
            SendWindow("plan-module", new { owner, id, error = ex.Message });
        }
    }
    // This read must never wait on stateLock. character:load builds a large bundle under
    // that lock and sends it over Electron IPC; waiting here can deadlock the renderer
    // exactly when it asks for the plan and the character bundle at the same time.
    void GetPlan(string owner)
    {
        // Immediate acknowledgement: proves the renderer->backend IPC path works and
        // prevents an indefinite spinner if plan creation later blocks on IO.
        SendWindow("plan-loading", new { owner });
        // Electron's renderer IPC must be emitted from the same request thread.
        // Sending plan-state from Task.Run can be silently dropped by Electron.NET,
        // leaving the renderer with the acknowledgement but no actual plan.
        try
        {
            var character = GetCharacterRequired(owner);
            var activity = FindActivity(owner);
            if (activity == null)
            {
                SendWindow("plan-unavailable", new { owner });
                return;
            }
            ContextPlan plan;
            var service = GetPlanService();
            // 计划文件丢失（例如 Plans 目录被清理）时，用自动保存的角色预设恢复。
            TryRestoreCharacterPreset(owner);
            // Never let a corrupt/cached plan file prevent opening the assembly page.
            try { plan = service.GetOrCreatePlan(owner); }
            catch (Exception createEx)
            {
                logger.LogWarning(createEx, "读取已有计划失败，改为重建默认计划 {Owner}", owner);
                plan = new ContextPlan
                {
                    Mode = "Off",
                    ApplyMacros = true,
                    UpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    Modules = new List<ContextPlanModule>
                    {
                        new()
                        {
                            Name = "角色设定 #0",
                            Source = "native",
                            Role = "System",
                            Content = BuildOfficialSystemPrompt(character, FindStorageRoot()),
                            Enabled = true
                        }
                    }
                };
            }
            ContextPromptText.MigrateFrameworkModules(plan, activity.ChatBot.ChatHistory);
            FillNativePlanPrompt(plan, ReadActiveCharacterFromDisk(character, FindStorageRoot()), FindStorageRoot());
            try { service.SavePlan(owner, plan); }
            catch (Exception saveEx) { logger.LogWarning(saveEx, "保存计划失败，但仍返回内存计划 {Owner}", owner); }
            SendPlan(owner, plan);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "读取上下文计划失败");
            SendWindow("plan-error", new { owner, message = ex.Message });
        }
    }
    void MakeDefaultPlan(string owner) => PlanOperation(owner, () => {
        var plan=GetPlanService().CreateDefaultPlan(owner); GetPlanService().SavePlan(owner,plan); SendPlan(owner,plan);
    });
    void SavePlanFromUi(string owner, JsonElement payload) => PlanOperation(owner, () => {
        var plan=PreparePlan(owner,payload); GetPlanService().SavePlan(owner,plan);
        AutoSaveCharacterPreset(owner, plan);
        SendPlan(owner,plan);
        SendWindow("plan-saved",new { owner, count=plan.Modules.Count });
    });
    ChatHistory SourceHistory(string owner) {
        var result=new ChatHistory();
        var activity=FindActivity(owner);
        if (activity!=null) { foreach(var message in activity.ChatBot.ChatHistory) result.Add(message); }
        else {
            result.AddSystemMessage(BuildOfficialSystemPrompt(ReadIndex(owner).ToObject<Character>()!,FindStorageRoot()));
            var dir=ResolveCharacterDirectory(GetCharacterRequired(owner),FindStorageRoot())!;
            var path=Path.Combine(dir,"Memory","History.json");
            if(File.Exists(path)) foreach(var row in JArray.Parse(File.ReadAllText(path)).OfType<JObject>())
                result.Add(new ChatMessageContent(new AuthorRole(ContextCompiler.Role(row["Role"]?["Label"]?.ToString())),row.Value<string>("Content") ?? ""));
        }
        return result;
    }
    void PreviewPlan(string owner, JsonElement payload) => PlanOperation(owner, () => {
        var plan=PreparePlan(owner,payload);
        var selectedMode=plan.Mode;
        // Preview the draft even before the user chooses an override mode.
        if(plan.Mode=="Off") plan.Mode="Temporary";
        var index=ReadIndex(owner);
        var unresolved=CollectUnresolvedMacros(plan,index,owner);
        var warnings=new List<string>();
        var result=ContextCompiler.Compile(SourceHistory(owner),plan,index,owner,warnings);
        SendWindow("plan-preview",new { owner, mode=selectedMode, unresolved, warnings, messages=result.Select((m,index)=>new {index,role=m.Role.Label,content=m.Content}) });
    });
    static void AtomicWrite(string path,string content) {
        File.WriteAllText(path+".tmp",content); File.Move(path+".tmp",path,true);
    }
    void ApplyPlan(string owner, JsonElement payload) => PlanOperation(owner, () => {
        ApplyPlanCore(owner, PreparePlan(owner, payload));
    });
    // 应用一份计划（“应用到当前角色”与“加载角色预设并立即生效”共用同一段逻辑）。
    void ApplyPlanCore(string owner, ContextPlan plan) {
        ContextTrace.Write($"apply start owner={owner} mode={plan.Mode} modules={plan.Modules.Count}");
        var index=ReadIndex(owner);
        // 逐模块展开一次：既能提前暴露结构错误，也顺带收集未识别的宏（原样保留，不中断）。
        var unresolved=CollectUnresolvedMacros(plan,index,owner);
        var activity=FindActivity(owner);
        if (plan.Mode=="Temporary" && activity!=null) {
            // 第三十二轮：这里以前会在挂载失败时抛「当前语言模型没有 ContextTransform 接口」，
            // 那是强依附别人插件的私有口子。现在挂的是官方 ChatBot.ChatSent 事件，
            // 任何语言模型都能用，没有“接口缺失”这回事 —— 所以不再抛异常，只记日志。
            var attached = AttachTransform(activity);
            ContextTrace.Write($"apply attach owner={owner} attached={attached}");
        }
        if (plan.Mode=="Permanent") {
            var localPrompt = RenderLocalOverride(owner, plan, index);
            var character = GetCharacterRequired(owner);
            var characterDirectory = ResolveCharacterDirectory(character, FindStorageRoot())!;
            var originalPromptPath = Path.Combine(characterDirectory, "ContextManagerOriginalPrompt.txt");
            if (!File.Exists(originalPromptPath))
                AtomicWrite(originalPromptPath, index.Value<string>("ContextManagerOriginalPrompt") ?? index.Value<string>("Prompt") ?? "");
            UpdateIndexJson(owner, jo => {
                if (jo["ContextManagerOriginalPrompt"]==null) jo["ContextManagerOriginalPrompt"]=index["ContextManagerOriginalPrompt"]?.DeepClone() ?? jo["Prompt"]?.DeepClone() ?? new JValue("");
                jo.Remove("ContextManagerPlan");
                jo["Prompt"]=localPrompt;
            });
            character.Prompt = localPrompt;
            var permanentPath=Path.Combine(ResolveCharacterDirectory(character,FindStorageRoot())!,"ContextAssembly.json");
            if(File.Exists(permanentPath)) File.Delete(permanentPath);
            if(File.Exists(TempPlanPath(owner))) File.Delete(TempPlanPath(owner));
            if (activity != null) {
                OnActivityDeactivated(activity); // local override must not require request-time transformation
                activity.ChatBot.EditChatHistory(thread => {
                    if (thread.ChatHistory.Count == 0) thread.ChatHistory.AddSystemMessage(BuildOfficialSystemPrompt(character, FindStorageRoot()));
                    else thread.ChatHistory[0].Content = BuildOfficialSystemPrompt(character, FindStorageRoot());
                }, "ContextManager 本地覆盖");
            }
        } else if(plan.Mode=="Temporary") AtomicWrite(TempPlanPath(owner),JsonConvert.SerializeObject(plan,Formatting.Indented));
        else {
            // 关闭：撤掉请求前重组，并清掉 index.json 里遗留的 ContextManagerPlan
            // （旧版遗留数据，留着会让“关闭”名不副实）。
            if(File.Exists(TempPlanPath(owner))) File.Delete(TempPlanPath(owner));
            if (activity != null) OnActivityDeactivated(activity);
            if (index["ContextManagerPlan"] != null)
                UpdateIndexJson(owner, jo => jo.Remove("ContextManagerPlan"));
        }
        DeleteTempOverride(owner); // retire legacy text-only injection
        GetPlanService().SavePlan(owner,plan);
        AutoSaveCharacterPreset(owner, plan);
        SendPlan(owner,plan);
        SendWindow("plan-applied",new { owner, mode=plan.Mode, unresolved });
        ContextTrace.Write($"apply done owner={owner} mode={plan.Mode} unresolved={unresolved.Count}");
        QueueInitialState();
    }
    void ResetPlan(string owner) => PlanOperation(owner, () => {
        var character = GetCharacterRequired(owner);
        var index = ReadIndex(owner);
        var original = index["ContextManagerOriginalPrompt"];
        var originalPromptPath = Path.Combine(ResolveCharacterDirectory(character, FindStorageRoot())!, "ContextManagerOriginalPrompt.txt");
        var originalText = original?.Value<string>() ?? (File.Exists(originalPromptPath) ? File.ReadAllText(originalPromptPath) : null);
        UpdateIndexJson(owner,jo=>{
            if(originalText!=null) jo["Prompt"]=originalText;
            jo.Remove("ContextManagerPlan"); jo.Remove("ContextManagerOriginalPrompt");
        });
        if (originalText != null) character.Prompt = originalText;
        if (File.Exists(originalPromptPath)) File.Delete(originalPromptPath);
        var permanentPath=Path.Combine(ResolveCharacterDirectory(GetCharacterRequired(owner),FindStorageRoot())!,"ContextAssembly.json");
        if(File.Exists(permanentPath)) File.Delete(permanentPath);
        if(File.Exists(TempPlanPath(owner))) File.Delete(TempPlanPath(owner));
        DeleteTempOverride(owner);
        var activity=FindActivity(owner);
        if(activity!=null) {
            var restored=BuildOfficialSystemPrompt(ReadIndex(owner).ToObject<Character>()!,FindStorageRoot());
            activity.ChatBot.EditChatHistory(thread=>{
                if(thread.ChatHistory.Count>0) {thread.ChatHistory[0].Content=restored;thread.ChatHistory[0].Role=AuthorRole.System;}
            },"ContextManager 还原角色设定");
        }
        var plan=GetPlanService().CreateDefaultPlan(owner); GetPlanService().SavePlan(owner,plan); SendPlan(owner,plan); QueueInitialState();
    });
    void ImportPlan(string owner,JsonElement payload) => PlanOperation(owner,()=>{
        if (FindActivity(owner) == null) throw new InvalidOperationException("请先激活角色再导入装配资源");
        var plan=PreparePlan(owner,payload);
        ImportPlanModules(owner,plan,JsonString(payload,"kind") ?? "card",JsonString(payload,"name"));
    });
    /// <param name="withCardWorldbook">
    /// 仅 kind=="card" 时有效：是否把角色卡内嵌的 <c>data.character_book</c> 也一起导进来。
    /// 为 true 时，卡内世界书的条目会和角色卡字段在同一次操作里写进计划。
    /// </param>
    void ImportPlanModules(string owner,ContextPlan plan,string kind,string? selectedName,bool withCardWorldbook=false) {
        var index=ReadIndex(owner);
        List<ContextPlanModule> modules;
        string? note=null; // 非错误、但需要告诉用户的说明（例如字段已被预设宏覆盖）
        if(kind=="preset") {
            if(string.IsNullOrWhiteSpace(selectedName)) throw new InvalidOperationException("请先选择预设，或导入预设 JSON 文件");
            modules=TavernImport.Preset(JObject.Parse(File.ReadAllText(Path.Combine(GetPresetDirectory(),SafeFileName(selectedName)+".json"))));
            if(modules.Count==0) throw new InvalidOperationException("该预设没有可导入的提示词条目（prompts 为空）。");
        } else if(kind=="worldbook") {
            var dir=ResolveCharacterDirectory(GetCharacterRequired(owner),FindStorageRoot())!;
            var path=Path.Combine(dir,"WorldBook.json");
            var book=File.Exists(path)?JObject.Parse(File.ReadAllText(path)):index["TavernCard"]?["character_book"];
            if(book==null) throw new InvalidOperationException("当前角色没有世界书：既没有 WorldBook.json，酒馆卡里也没有内嵌 character_book。请先点“世界书 → 选择 JSON 文件”导入。");
            modules=TavernImport.World(book);
            if(modules.Count==0) throw new InvalidOperationException("世界书里没有可导入的条目（entries 为空）。");
        } else if(kind=="card") {
            var card=index["TavernCard"] as JObject ?? throw new InvalidOperationException(
                "当前角色没有酒馆卡数据（缺少 TavernCard.json）。请用本页的「角色卡」按钮选择一个 JSON/PNG 角色卡导入后再试。");
            modules=new();
            var skippedByPreset=new List<string>();
            var skippedByNative=new List<string>();
            // 「角色设定 #0」保存的是**完整官方系统消息**（名称 / 生日 / 简介 / 设定 / 私人文件夹），
            // 里面本来就含有角色的简介与 Prompt 正文。角色卡字段若与它逐字相同，就没必要再发一遍。
            var nativeText=string.Join("\n",plan.Modules.Where(m=>m.Source=="native" && m.Enabled).Select(m=>m.Content ?? ""));
            foreach(var (key,label,macro) in TavernImport.CardFields) {
                var content=card.Value<string>(key);
                if(string.IsNullOrWhiteSpace(content)) continue;
                // 预设里已经用同名宏占位时，再导入一次角色卡字段只会把同样的内容发两遍。
                if(plan.Modules.Any(m=>m.Source=="preset" && m.Content.Contains(macro))) { skippedByPreset.Add(label); continue; }
                if(nativeText.Length>0 && nativeText.Contains(content.Trim())) { skippedByNative.Add(label); continue; }
                // 模块名带来源前缀，配合装配页的按来源分区，避免只看到“描述/性格”而不知道来自哪一层。
                modules.Add(new ContextPlanModule { Name="角色卡 · " + label,Content=content,Source="card" });
            }
            // 多条开场白：first_mes 已经在上面作为「开场白」进来了，这里补 alternate_greetings。
            // 以前只取第一条，其余开场白被静默丢掉 —— 用户看得见卡里有好几条，导入后却只剩一条。
            // 现在每条额外开场白都单独成一个模块（「角色卡 · 开场白 2 / 3 …」），
            // 想用哪条就勾哪条，其余取消勾选即可。
            var firstMes = (card.Value<string>("first_mes") ?? "").Trim();
            var greetings = TavernImport.Greetings(card);
            var extraGreetings = 0;
            foreach(var (label,text) in greetings) {
                // 与 first_mes 相同的那条由上面的字段循环负责（含「已由预设宏提供」判定）。
                if(firstMes.Length>0 && text == firstMes) continue;
                if(nativeText.Length>0 && nativeText.Contains(text)) { skippedByNative.Add(label); continue; }
                modules.Add(new ContextPlanModule { Name="角色卡 · " + label, Content=text, Source="card" });
                extraGreetings++;
            }
            // 卡内世界书：有些卡把剧情设定全放在 character_book 里，只导角色卡字段等于丢了一半。
            var cardBookModules=new List<ContextPlanModule>();
            if(withCardWorldbook) {
                cardBookModules=TavernImport.World(card["character_book"]);
                modules.AddRange(cardBookModules);
            }
            var notes=new List<string>();
            if(skippedByPreset.Count>0) notes.Add("以下字段已由预设宏提供，未重复导入：" + string.Join("、", skippedByPreset));
            if(skippedByNative.Count>0) notes.Add("以下字段与当前「角色设定 #0」的内容相同，未重复导入：" + string.Join("、", skippedByNative));
            if(withCardWorldbook) notes.Add(cardBookModules.Count>0 ? $"卡内世界书已一并导入 {cardBookModules.Count} 条。" : "这张卡里没有可用的内嵌世界书（character_book 为空）。");
            if(extraGreetings>0) notes.Add($"这张卡有 {greetings.Count} 条开场白，已分别导入（角色卡 · 开场白 2 起）。只勾选你想用的那条即可。");
            // 「特殊卡」：标准提示词字段一个都没填。这不是错误 —— 开场白 / 作者注释 / 卡内世界书
            // 本身也是设定内容，用户要的是「能适配基本信息就行」，不该在这里直接拒绝导入。
            if(!TavernImport.CardFields.Take(TavernImport.StandardFieldCount).Any(f=>!string.IsNullOrWhiteSpace(card.Value<string>(f.Key))))
                notes.Add("这张卡没有填写标准的提示词字段（描述 / 性格 / 场景 / 系统指令 / 对话示例 / 历史后指令），已按「基本信息」导入：只用上了开场白 / 作者注释等能读到的内容，之后可再补设定或导入世界书。");
            if(modules.Count==0) {
                if(skippedByPreset.Count>0)
                    throw new InvalidOperationException(
                        "角色卡的「" + string.Join("、", skippedByPreset) + "」已由预设中的 {{description}}/{{personality}} 等宏提供，无需重复导入。\n\n"
                        + "你在预览里看到的角色卡内容，正是这些宏展开的结果。如果只想单独编辑角色卡字段，请先移除预设里对应的宏占位条目。");
                if(skippedByNative.Count>0)
                    throw new InvalidOperationException(
                        "角色卡的「" + string.Join("、", skippedByNative) + "」与当前「角色设定 #0」的内容完全相同，已经包含在发送内容里，无需重复导入。\n\n"
                        + "如果确实想单独编辑这些字段，请先取消勾选或删除「角色设定」分区里的模块，再导入一次。");
                throw new InvalidOperationException(
                    "这张角色卡里没有任何可用文本（描述 / 性格 / 场景 / 系统指令 / 对话示例 / 历史后指令 / 开场白（含额外开场白）/ 作者注释 全为空，也没有内嵌世界书）。");
            }
            if(notes.Count>0) note=string.Join("\n", notes);
            // ⚠️ 这里**不能**移除 native 模块。
            // 以前写成「酒馆模式替换角色设定」，直接 plan.Modules.RemoveAll(Source=="native")，
            // 结果是：导入后「角色设定」分区立刻从装配页消失，而 PreparePlan 在下一次保存/应用时
            // 又会把 native 模块补回来 → 层级忽隐忽现（用户报的「点一下角色卡不参与装配它才出现」）。
            // 重复内容的处理交给上面的 skippedByNative，不做静默删除。
        } else throw new InvalidOperationException("未知导入类型："+kind);
        // 同来源的旧模块一律替换；卡内世界书一并导入时，worldbook 来源也一起换掉，避免残留上一次的条目。
        var replaced=withCardWorldbook ? new[]{"card","worldbook"} : new[]{kind};
        plan.Modules.RemoveAll(m=>replaced.Contains(m.Source));
        int at=plan.Modules.FindLastIndex(m=>m.Group=="system")+1; plan.Modules.InsertRange(at,modules);
        GetPlanService().SavePlan(owner,plan);
        // 先下发计划、再发「导入完成」。
        // 反过来写会出事：计划被拒发时 SendPlan 会先发 plan-error 弹窗，紧接着 plan-imported
        // 又把那个弹窗顶掉 —— 用户只看到「导入完成」，装配页却一片空白（2026-09-30 反馈）。
        // 现在计划不会再被拒发（超预算只瘦身），但顺序仍然保持这个方向：
        // 任何失败提示都必须晚于成功提示，才不会被顶掉。
        var shrunk=SendPlan(owner,plan);
        if(shrunk>0) note=(note==null ? "" : note + "\n") + $"有 {shrunk} 个模块的正文超过 {ContextStateBudget.MinKeepChars} 字，只随报文下发了预览（模块卡片上会标出来）。点开卡片即可读取全文，保存/预览/应用时后端会按 Id 把正文回填，不会丢内容。";
        SendWindow("plan-imported",new { owner, kind, name=selectedName, count=modules.Count, note, shrunk, truncated=shrunk>0 });
    }
    async void ImportPlanFile(string owner,JsonElement payload) {
        try {
            if (FindActivity(owner)==null) throw new InvalidOperationException("请先激活角色再导入装配资源");
            var plan=PreparePlan(owner,payload);
            var kind=JsonString(payload,"kind") ?? "preset";
            if(kind is not ("preset" or "card" or "worldbook")) throw new InvalidOperationException("未知导入类型："+kind);
            var parent=windowService?.Window ?? throw new InvalidOperationException("没有可用的插件窗口");
            var extensions=kind=="card" ? new[]{"json","png"} : new[]{"json"};
            ContextTrace.Write($"import dialog open kind={kind} owner={owner}");
            var files=await Electron.Dialog.ShowOpenDialogAsync(parent,new OpenDialogOptions {
                Title=kind switch {"preset"=>"选择酒馆预设","card"=>"选择酒馆角色卡",_=>"选择世界书"},
                ButtonLabel="导入", Filters=new[]{new FileFilter {Name="酒馆资源",Extensions=extensions}},
                Properties=new[]{OpenDialogProperty.openFile}
            });
            var file=files?.FirstOrDefault();
            ContextTrace.Write($"import dialog result={file ?? "<empty>"}");
            if(string.IsNullOrWhiteSpace(file)) { SendWindow("plan-import-cancelled",new { owner }); return; }

            // ── 角色卡：先在**锁外**把卡读出来，检测卡里有没有内嵌世界书 ──
            // 有就问用户「要不要一起导入」（2026-09-30 需求：不该让用户先在工具栏猜这张卡有没有世界书）。
            // 弹窗是 await 的，所以这段必须在 lock(stateLock) 之外 —— 锁里不能 await。
            JObject? cardData=null;
            var withCardWorldbook=false;
            if(kind=="card") {
                var root=TavernImport.ReadCard(file);
                cardData=root["data"] as JObject ?? root;
                if(!cardData.Properties().Any()) throw new InvalidOperationException("角色卡为空");
                var bookCount=TavernImport.EntryRows(cardData["character_book"]).Count();
                if(bookCount>0) {
                    var display=cardData.Value<string>("name") ?? Path.GetFileNameWithoutExtension(file);
                    withCardWorldbook=await AskCardWorldbookAsync(display,cardData["character_book"],bookCount);
                }
            }

            lock(stateLock) {
                string? presetName=null;
                string? worldbookNote=null;
                if(kind=="preset") {
                    var root=JObject.Parse(File.ReadAllText(file));
                    TavernImport.Preset(root); // validate before saving
                    presetName=UniquePresetName(Path.GetFileNameWithoutExtension(file));
                    AtomicWrite(Path.Combine(GetPresetDirectory(),SafeFileName(presetName)+".json"),root.ToString(Formatting.Indented));
                } else {
                    var dir=ResolveCharacterDirectory(GetCharacterRequired(owner),FindStorageRoot()) ?? throw new InvalidOperationException("角色目录不存在");
                    if(kind=="card") {
                        var target=Path.Combine(dir,"TavernCard.json");
                        if(File.Exists(target)) File.Copy(target,target+".backup-"+DateTime.Now.ToString("yyyyMMddHHmmss"));
                        AtomicWrite(target,cardData!.ToString(Formatting.Indented));
                        if(withCardWorldbook) worldbookNote=MergeCardWorldBook(dir,cardData["character_book"]);
                    } else {
                        var book=JObject.Parse(File.ReadAllText(file));
                        if(TavernImport.World(book).Count==0) throw new InvalidOperationException("世界书没有可导入条目");
                        var target=Path.Combine(dir,"WorldBook.json");
                        if(File.Exists(target)) File.Copy(target,target+".backup-"+DateTime.Now.ToString("yyyyMMddHHmmss"));
                        // 规范化后再落盘：酒馆世界书用小写的 entries/comment/keys，本插件的世界书面板
                        // 读的是 Entries/Title/Keywords。原文直写会出现「导入成功但面板一条都没有」。
                        AtomicWrite(target,TavernImport.WorldBookFile(book).ToString(Formatting.Indented));
                    }
                }
                ImportPlanModules(owner,plan,kind,presetName,withCardWorldbook);
                if(presetName!=null) ListPresets();
                if(worldbookNote!=null) {
                    // 世界书文件已经变了，让世界书面板和概览重新读一次。
                    GetWorldBook(owner);
                    ContextTrace.Write($"card worldbook import owner={owner} {worldbookNote}");
                }
            }
        } catch(Exception ex) {logger.LogError(ex,"导入装配资源失败 {Owner}",owner);SendWindow("plan-error",new { owner, message=ex.Message });}
    }

    /// <summary>
    /// 把角色卡内嵌的 <c>data.character_book</c> 合并进角色目录的 WorldBook.json。
    /// 返回给用户看的一句话说明（null 表示这张卡没有内嵌世界书，什么都没做）。
    /// </summary>
    string? MergeCardWorldBook(string characterDirectory, JToken? cardBook)
    {
        var total=TavernImport.EntryRows(cardBook).Count();
        if(total==0) return null;
        var path=Path.Combine(characterDirectory,"WorldBook.json");
        JObject? existing=null;
        if(File.Exists(path)) {
            try { existing=JObject.Parse(File.ReadAllText(path)); }
            catch(Exception ex) { ContextTrace.Write("card worldbook existing parse failed: "+ex.Message); }
        }
        // ⚠️ 这里传**原始** cardBook。MergeWorldBook 内部会规范化，且 NormalizeEntries 是幂等的。
        // 2026-09-30 的崩溃就是「先 NormalizeEntries 得到 JArray，再喂给 MergeWorldBook」——
        // 后者二次规范化，EntryRows 在 JArray 上取 ["entries"] 抛 ArgumentException，
        // 任何带 character_book 的卡都必炸。
        var (added,skipped,book)=TavernImport.MergeWorldBook(existing,cardBook);
        if(added==0) return $"卡内世界书 {total} 条已全部存在，未重复写入。";
        if(existing!=null) File.Copy(path,path+".backup-"+DateTime.Now.ToString("yyyyMMddHHmmss"));
        AtomicWrite(path,book.ToString(Formatting.Indented));
        return $"卡内世界书新增 {added} 条" + (skipped>0 ? $"，跳过 {skipped} 条（重复或空内容）" : "") + "。";
    }

    // ===== 导入选项：卡内世界书要不要导 =====
    // 只存「用户对导入弹窗的长期选择」，不塞进 ContextManagerConfig ——
    // 那是模块级配置（覆盖模式 / 预设 / 宏开关），改它会牵动配置面板的读写与迁移。
    // 这里是一个 3 行 JSON 的小文件，读失败就退回默认值，不影响导入本身。
    static readonly object importOptionsLock=new();
    string ImportOptionsPath() => Path.Combine(FindStorageRoot() ?? throw new InvalidOperationException("找不到 Storage 根目录"),"ContextManager","ImportOptions.json");

    string ImportOption(string key,string fallback) {
        try {
            var path=ImportOptionsPath();
            if(!File.Exists(path)) return fallback;
            return JObject.Parse(File.ReadAllText(path)).Value<string>(key) is string value && value.Length>0 ? value : fallback;
        } catch(Exception ex) { ContextTrace.Write("import options read failed: "+ex.Message); return fallback; }
    }
    void SaveImportOption(string key,string value) {
        try {
            lock(importOptionsLock) {
                var path=ImportOptionsPath();
                JObject? jo=null;
                if(File.Exists(path)) { try { jo=JObject.Parse(File.ReadAllText(path)); } catch(Exception ex) { ContextTrace.Write("import options parse failed: "+ex.Message); } }
                jo??=new JObject();
                jo[key]=value; jo["UpdatedAt"]=DateTime.Now;
                AtomicWrite(path,jo.ToString(Formatting.Indented));
            }
        } catch(Exception ex) { ContextTrace.Write("import options save failed: "+ex.Message); }
    }
    /// <summary>卡内世界书策略：<c>ask</c>（每次弹窗，默认）/ <c>always</c> / <c>never</c>。</summary>
    string CardWorldbookMode() {
        var mode=ImportOption("cardWorldbook","ask");
        return mode is "always" or "never" ? mode : "ask";
    }

    // ── 「后端问、前端答」：让插件用自己的弹窗问用户 ──────────────────────────
    //
    // 以前这里是 Electron.Dialog.ShowMessageBoxAsync —— 那是 **Windows 原生对话框**：
    // 灰底、系统字体、系统按钮布局，和插件自己的界面完全不是一套，用户一眼就看得出
    // 「这是外来的东西」。而且它的标题栏写的是应用名，不是插件名。
    //
    // 现在改成走 IPC：后端发一条问询报文，前端用插件自己的 openModal 渲染，
    // 用户点完把答案送回来，后端用 TaskCompletionSource 接住。
    //
    // 两条时序约束：
    //   1. **绝不能持 stateLock 等待**（和文件对话框同理，见 ImportPlanFile 的分层注释）；
    //   2. 必须能收场 —— 用户把插件窗口关掉时永远不会有人回答，所以这里每 5 秒检查一次
    //      窗口是否还活着，关了就当「没选」放行，绝不能让导入流程永久挂住。
    sealed record UiAnswer(bool Accepted, bool Remember);

    readonly Dictionary<string, TaskCompletionSource<UiAnswer>> pendingUiAsks = new(StringComparer.Ordinal);
    readonly object uiAskGate = new();

    /// <summary>发一条问询给渲染进程并等它回答。窗口关掉时按「没选」放行。</summary>
    async Task<UiAnswer> AskUiAsync(string askType, Dictionary<string, object?> payload, string fallbackReason)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<UiAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (uiAskGate) pendingUiAsks[requestId] = tcs;
        try
        {
            payload["requestId"] = requestId;
            SendWindow(askType, payload);
            ContextTrace.Write($"ui ask sent type={askType} id={requestId}");
            while (true)
            {
                if (await Task.WhenAny(tcs.Task, Task.Delay(5000)) == tcs.Task) return await tcs.Task;
                if (windowService?.Window == null)
                {
                    ContextTrace.Write($"ui ask aborted type={askType} id={requestId}（插件窗口已关闭，{fallbackReason}）");
                    return new UiAnswer(false, false);
                }
            }
        }
        finally
        {
            lock (uiAskGate) pendingUiAsks.Remove(requestId);
        }
    }

    /// <summary>渲染进程对问询的回答。迟到的答案（已超时 / 已取消）只记日志。</summary>
    void AnswerUiAsk(JsonElement payload)
    {
        var id = JsonString(payload, "requestId") ?? "";
        TaskCompletionSource<UiAnswer>? tcs;
        lock (uiAskGate)
        {
            if (!pendingUiAsks.TryGetValue(id, out tcs)) tcs = null;
            else pendingUiAsks.Remove(id);
        }
        if (tcs == null)
        {
            ContextTrace.Write($"ui ask answer ignored id={id}（没有对应的待回问询：可能已经取消，或来自上一次插件加载）");
            return;
        }
        var accepted = payload.TryGetProperty("accepted", out var acc) && acc.ValueKind == JsonValueKind.True;
        var remember = payload.TryGetProperty("remember", out var rem) && rem.ValueKind == JsonValueKind.True;
        tcs.TrySetResult(new UiAnswer(accepted, remember));
    }

    /// <summary>
    /// 「这张卡里内嵌了世界书，要一起导入吗？」确认弹窗。返回 true = 一起导入。
    ///
    /// 策略来自 ImportOptions.cardWorldbook：
    /// <list type="bullet">
    /// <item><c>always</c> —— 直接导入，不打扰用户</item>
    /// <item><c>never</c> —— 直接跳过</item>
    /// <item><c>ask</c> —— 弹窗（默认）。勾选「记住我的选择」后自动改成 always / never</item>
    /// </list>
    ///
    /// <para>弹窗用的是**插件自己的** openModal（前端 `showCardWorldbookAsk`），不是原生对话框。
    /// 用户把它关掉（点 × / 点遮罩）等价于「只要角色卡」，而不是取消整个导入 ——
    /// 文件已经选好了，把角色卡导进来才是用户的本意。</para>
    /// </summary>
    async Task<bool> AskCardWorldbookAsync(string displayName,JToken? cardBook,int bookCount) {
        var mode=CardWorldbookMode();
        if(mode=="always") return true;
        if(mode=="never") return false;
        // 只给前几条做预览：弹窗是给用户「确认有没有这回事」的，不是让他读完整本世界书。
        var titles=TavernImport.EntryTitles(cardBook);
        var payload=new Dictionary<string,object?>(StringComparer.Ordinal) {
            ["displayName"]=displayName,
            ["bookCount"]=bookCount,
            ["preview"]=titles
        };
        var answer=await AskUiAsync("card-worldbook-ask",payload,"按「只要角色卡」继续");
        if(answer.Remember) SaveImportOption("cardWorldbook",answer.Accepted ? "always" : "never");
        ContextTrace.Write($"card worldbook decision display={displayName} count={bookCount} import={answer.Accepted} remember={answer.Remember}");
        return answer.Accepted;
    }
    async void ImportWorldBook(string owner) {
        try {
            var files=await Electron.Dialog.ShowOpenDialogAsync(windowService!.Window!,new OpenDialogOptions {Title="导入世界书",Filters=new[]{new FileFilter {Name="JSON",Extensions=new[]{"json"}}},Properties=new[]{OpenDialogProperty.openFile}});
            if(files.Length==0) return;
            var book=JObject.Parse(File.ReadAllText(files[0])); TavernImport.World(book);
            var dir=ResolveCharacterDirectory(GetCharacterRequired(owner),FindStorageRoot())!;
            // 规范化后再落盘，否则世界书面板读不到小写的 entries/comment/keys。
            AtomicWrite(Path.Combine(dir,"WorldBook.json"),TavernImport.WorldBookFile(book).ToString()); GetWorldBook(owner);
        } catch(Exception ex) {SendWindow("error",new {message=ex.Message});}
    }
}
