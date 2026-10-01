using System;
using System.ComponentModel;
using System.Threading.Tasks;
using Alife.Framework;
using Alife.Function.FunctionCaller;
using Microsoft.Extensions.Logging;

namespace Marisa.ContextManager;

[Module(
    "上下文管理器",
    "「这一次请求到底发了什么给模型」的全景台：把角色预设、系统提示词、记忆、对话历史、便签、技能、"
    + "多模态一起摊在一条时间线上，可以逐条增删改、排序、开关参与装配，"
    + "还能把当前这套装配存成快照随时切换。",
    defaultCategory: "Marisa",
    editorUI: typeof(ContextManagerUI))]
[Description("上下文管理器：把「这一次请求到底发了什么给模型」摊开给用户看，并把上下文装配的主动权交还给它。"
    + "打开下面两个开关后，角色自己也可以查看和修改上下文。")]
public class ContextManagerModule(
    XmlFunctionCaller functionCaller,
    CharacterSystem characterSystem,
    ChatActivitySystem chatActivitySystem,
    StorageSystem storageSystem,
    PluginSystem pluginSystem,
    ModuleSystem moduleSystem,
    Interactor<ContextManagerModule> interactor,
    ILogger<ContextManagerModule> logger) :
    ChatBehaviour,
    IConfigurable<ContextManagerConfig>
{
    public ContextManagerConfig Configuration { get; set; } = new();
    public ContextManagerRuntime? Runtime { get; private set; }

    // 角色侧接口：函数表 + 提示词。见 ContextAgentApi。
    XmlHandler? handler;
    ContextAgentApi? agentApi;

    /// <summary>上一次写进提示词的正文。只在「内容真的变了」时才重写，避免无谓地搅动历史。</summary>
    string promptSnapshot = "";

    /// <summary>
    /// 把说明写进历史里的那一行。
    ///
    /// <para><b>⚠️⚠️ 这里是本项目走过最多弯路的地方，结论只有一个字：<i>别自己管</i>。</b></para>
    ///
    /// <para><b>错误做法（曾经用了整整三轮）</b>：绕开 <c>Interactor</c>，自己持有
    /// <c>ChatMessageContent</c>、自己 <c>EditChatHistory</c> 插/改/删，理由是「
    /// <c>Interactor.Dispose()</c> 只 Remove 不清 <c>promptContent</c>，会留孤儿」。
    /// 结果连踩三坑：</para>
    /// <list type="number">
    /// <item>自己维护的「在不在历史里」判断（<c>ReferenceEquals</c>）**与真实发送的那份历史不同步**
    /// —— 插件这边一直说 <c>already in history</c>，而 AI 的上下文里根本没有它；</item>
    /// <item>自己写去重/认领逻辑，越写越复杂，每加一层就多一个「什么时候该重写」的隐含假设；</item>
    /// <item><c>OnUpdate</c> 里高频调 <c>RefreshAgentPrompt</c>，每秒刷三次日志，反而掩盖了真问题。</item>
    /// </list>
    ///
    /// <para><b>正确做法（照抄 Alife 官方 <c>VirtualWorldService</c>）</b>：
    /// <c>OnAwake</c> 里调一次 <c>interactor.Prompt(正文)</c>，然后<b>什么都不用管</b>。</para>
    ///
    /// <list type="bullet">
    /// <item><c>Prompt()</c> 内部会在**第一次**调用时找到/新建那条 <c>[功能说明(类型名)]</c> 消息，
    /// 之后每次调用只是**覆写它的正文** —— 天然幂等，重排、裁剪都能自愈；</item>
    /// <item><b>绝不调 <c>interactor.Dispose()</c></b>：官方插件没有一个调的。
    /// 那个「孤儿缓存」坑之所以会发生，正是因为**自己主动 Dispose 过**；
    /// 不主动 Dispose，<c>promptContent</c> 就始终指向历史里那条真实消息。</item>
    /// <item>要随配置变化更新正文时，再调一次 <c>Prompt()</c> 即可（本类在
    /// <c>OnUpdate</c> 里比对 <see cref="promptSnapshot"/>，只在内容真变时才写）。</item>
    /// </list>
    ///
    /// <para><c>ContextManagerRuntime</c> 不需要我们喂历史：它自己订阅
    /// <c>ChatBot.ChatHistoryEdited</c> 事件（见 <c>SubscribeHistory</c>），
    /// 历史一变就重建快照推给窗口。</para>
    /// </summary>
    void UpdateAgentPrompt()
    {
        if (agentApi == null) return;
        try
        {
            // ⚠️ 必须把 handler 传进去。BuildPrompt 会调 handler.FunctionDocument()
            // 把 12 个函数的签名文档拼进提示词 —— 本模块是用 RegisterHandlerWithoutDocument
            // 注册的（DocumentMode.None），框架的 XmlFunctionCaller 不会替我们注入，
            // 只能自己拼。（详细理由见 ContextAgentApi.BuildPrompt 里的注释。）
            var text = agentApi.BuildPrompt(handler);
            var body = string.IsNullOrWhiteSpace(text) ? "" : PromptTag + "\n" + text;
            // 两个权限开关都关时 BuildPrompt 返回空串。此时**什么都不做**：
            // 已经写进去的那条保持原样即可。不去 Remove —— 因为「把这个功能从 AI 的认知里抹掉」
            // 不是我们该干的事（关掉开关只是「角色不能再改上下文」，不是「角色不该知道有这个功能」）。
            if (body.Length == 0) return;
            if (body == promptSnapshot) return;   // 内容没变，不搅动历史
            promptSnapshot = body;
            interactor.Prompt(body);              // ← 就这一行，其余全交给框架
            ContextTrace.Write($"agent prompt: written (len={body.Length})");
        }
        catch (Exception ex)
        {
            // ⚠️ `ChatBehaviour.AwakeAsync` 会把 OnAwake 的异常整个 catch 掉、只记 AlifeLog
            // （不进插件 trace），所以这里必须自己兜住，否则出问题时我们完全看不到。
            ContextTrace.Write($"agent prompt FAILED: {ex.GetType().Name}: {ex.Message}");
        }
    }

    protected override Task OnAwake()
    {
        // ⚠️ 第一件事挂兜底日志目录：`ContextTrace.Write` 在 `Configure(storageRoot)` 之前会直接
        // return，而 `Configure` 只在**打开插件窗口**时才调用 —— OnAwake 阶段（最容易出错的一段）
        // 的日志本来会被**静默丢弃**，排查时完全瞎。见 ContextTrace.ConfigureFallback。
        ContextTrace.ConfigureFallback(SafePluginDir());
        ContextTrace.Write($"module awake: start owner={SafeName()}");
        try
        {
            Runtime = ContextManagerRuntime.GetOrCreate(characterSystem, chatActivitySystem, storageSystem, pluginSystem, moduleSystem, logger);
            // ⚠️ 必须**按角色**登记配置：Runtime 是全局单例，本角色的 Configuration 不能
            // 覆盖掉别人的（否则多角色时互相冲掉权限，见 ContextManagerRuntime.SetOwnerConfig）。
            Runtime.SetOwnerConfig(SafeName(), Configuration);

            // 角色侧接口：api + 函数表都**按本模块实例**持有（每个激活的角色各有一份），
            // 各自注册到各自的 XmlFunctionCaller 上，互不干扰。
            agentApi = new ContextAgentApi(Runtime, Character, interactor);
            handler = new XmlHandler(agentApi);
            functionCaller.RegisterHandlerWithoutDocument(handler, DestroyCancellationToken);

            // ⚠️ 顺序很重要：**先注册函数表，再写提示词**。
            // 提示词正文里包含 `handler.FunctionDocument()`（12 个函数的签名），
            // 反过来的话第一次写进去的就是一份没有函数列表的残缺文档。
            UpdateAgentPrompt();

            ContextTrace.Write($"module awake: ok owner={SafeName()} self={Configuration.AllowModifySelf} others={Configuration.AllowModifyOthers} promptLen={promptSnapshot.Length}");
        }
        catch (Exception ex)
        {
            ContextTrace.Write($"module awake FAILED: {ex.GetType().Name}: {ex.Message} @ {ex.StackTrace}");
        }
        return Task.CompletedTask;
    }

    string SafeName()
    {
        try { return Character?.Name ?? "?"; } catch { return "?"; }
    }

    /// <summary>插件自己的目录（兜底日志用）。取不到就返回 null（那就退回静默，不抛）。</summary>
    string? SafePluginDir()
    {
        try { return pluginSystem.PluginContext.GetPluginDirectoryPath("Marisa.ContextManager"); }
        catch { return null; }
    }

    protected override Task OnUpdate()
    {
        try
        {
            Runtime?.SetOwnerConfig(SafeName(), Configuration);
            // 只在「配置变了导致正文变了」时才真正写历史（UpdateAgentPrompt 内部用
            // promptSnapshot 短路），所以这里每帧调用是安全的 —— 不会搅动历史。
            UpdateAgentPrompt();
        }
        catch (Exception ex)
        {
            ContextTrace.Write($"module update FAILED: {ex.GetType().Name}: {ex.Message}");
        }
        return Task.CompletedTask;
    }

    /// <summary>提示词消息的标题行。与框架 <c>Interactor</c> 用的完全一致（由它生成）。</summary>
    static string PromptTag => Interactor<ContextManagerModule>.GetPromptTag();

    public Task OpenWindowAsync() => Runtime?.OpenWindowAsync() ?? Task.CompletedTask;

    protected override Task OnDestroy()
    {
        // ⚠️ 这里**故意什么都不做**（与官方 VirtualWorldService 一致）。
        //
        // 曾经这里调 RemoveFromHistory()（后来）和 interactor.Dispose()（更早），两个都是错的：
        //   · `Dispose()` 只把消息 Remove 出历史、**不清空内部 `promptContent` 字段** ——
        //     于是那个引用变成「不在历史里、但字段还指着它」的**孤儿**；此后 Prompt() 只写孤儿，
        //     历史里永远不再出现这段提示词。
        //   · 自己 Remove 也一样：卸载插件时把说明摘掉的意义不大（下次激活会重插），
        //     却引入了「什么时候摘、摘哪条」的判断成本。
        // 插件被禁用时框架会整体重建历史；正常重启时 OnAwake 会再写一次。交给框架最稳。
        handler = null;
        agentApi = null;
        promptSnapshot = "";
        Runtime?.Release();
        Runtime = null;
        return Task.CompletedTask;
    }
}
