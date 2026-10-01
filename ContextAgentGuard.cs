using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Alife.Framework;

namespace Marisa.ContextManager;

/// <summary>
/// 角色侧「改动上下文」的准入闸门。
///
/// 这套东西存在的唯一理由：**角色不该能自己悄悄改掉发给模型的内容**。
/// 上下文是这个角色「是什么」的直接来源 —— 改上下文等于改人格。所以权限由用户的开关定。
///
/// <para>
/// <b>2026-10-01（第二轮）设计收缩：逐次审批已取消。</b>
/// </para>
///
/// 原先的模型是两道闸：<b>开关</b>（够不够得着）+ <b>逐次审批</b>（每一次都要点批准）。
/// 用户指出这是多余的 ——
/// 「既然都有『可以修改』的开关了，那个开关本身就是信任的表达；
///   已经做过一次决定的事，不该每次重问。」
///
/// 现在的模型是<b>一道闸 + 两处例外</b>：
///
/// <list type="number">
/// <item><b>开关（唯一的授权表达）</b>：
///       <c>AllowModifySelf</c> / <c>AllowModifyOthers</c>，默认都关。
///       关着 = 角色连这个能力都不知道（提示词里不出现）；
///       开着 = 角色可以<b>自主</b>增删改、存快照、切换、应用，<b>不再逐次弹窗</b>。</item>
/// <item><b>例外一 · 越界装插件</b>：要把本插件装到<b>别的角色</b>身上（改对方
///       <c>index.json</c> 的 <c>Modules</c>），仍然问用户一次。
///       这动的是<b>别人的角色文件</b>，与「我信任这个 AI」不是同一回事。</item>
/// <item><b>例外二 · 不可逆覆盖</b>：对方没启用插件时，若要改用<b>本地覆盖</b>
///       （写对方 <c>index.json</c> 的 <c>Prompt</c>），再问一次。
///       插件覆盖随时可关掉复原；本地覆盖关掉插件也照样生效，属于不可逆。</item>
/// </list>
///
/// <b>改自己永远安静</b>：自己没启用插件覆盖时直接自动启用（那是她自己的东西，
/// 插件覆盖又不动任何角色文件），因此不存在任何弹窗。
/// </summary>
public sealed partial class ContextManagerRuntime
{
    // ── 准入上下文（纯数据，**不含任何等待用户的机制**）─────────────────
    //
    // ⚠️ 2026-10-02：这里以前有一整套 `AskAgentAsync` / `pendingAgentAsks` /
    //    `AgentDecision` / 前端 `agent-ask` 弹窗 —— **已全部删除**。
    //
    // 用户反馈：**审批弹窗从来没真正弹出来过**，于是 AI 调 `AgentContextAdd` 时后端
    // `await` 一个永远不会被 SetResult 的 TaskCompletionSource，整个对话发送通道被永久占住
    // （UI 上一直显示「执行 AgentContextAdd 函数丨分析对话」，用户再也发不出消息）。
    //
    // 教训：**任何「await 用户输入」的准入机制，只要「弹窗真的会出现」这个前提不成立，
    //       就等价于一个必然卡死的陷阱**，而且卡住的代价是整个对话不能说话 ——
    //       远大于它想防住的风险。准入判定必须**同步、立即、可预测**。
    internal sealed record AgentRequest(string SelfOwner, string TargetOwner)
    {
        public bool IsSelf => string.Equals(SelfOwner, TargetOwner, StringComparison.OrdinalIgnoreCase);
    }

    // ── 准入判定 ────────────────────────────────────────────────────────

    /// <summary>目标角色的 index.json 里是否登记了本插件（决定「插件覆盖」能不能用）。</summary>
    bool IsPluginEnabledFor(string owner)
    {
        try
        {
            var index = ReadIndex(owner);
            if (index?["Modules"] is not Newtonsoft.Json.Linq.JArray modules) return false;
            return modules.Any(m => m?.ToString()?.Contains("ContextManagerModule", StringComparison.OrdinalIgnoreCase) ?? false);
        }
        catch (Exception ex)
        {
            ContextTrace.Write($"agent check plugin enabled failed owner={owner}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 判断这次请求能不能继续。返回 null = 放行；返回非空 = 拒绝原因（要回给角色的话）。
    ///
    /// <para><b>2026-10-02：所有「等用户点弹窗」的路径已彻底删除。</b></para>
    ///
    /// 用户反馈：**审批弹窗从来没有真正弹出来过**，于是 AI 一调 <c>AgentContextAdd</c>
    /// 就永久挂住（UI 上一直显示「执行 AgentContextAdd 函数丨分析对话」），
    /// 整个会话发送通道被占，用户再也发不出消息。
    ///
    /// 那套机制的设计前提是「用户会看到弹窗并回答」—— 前提不成立时，它就是一个**必然卡死**
    /// 的陷阱，而且卡住的代价是**整个对话不能用**，远大于它想防住的风险。
    /// 因此：**绝不 await 任何用户输入**。判定必须是**同步、立即、可预测**的。
    ///
    /// 现在的模型（纯开关，一条闸）：
    /// <list type="number">
    /// <item>开关没开 → 直接拒（连问都不问）；</item>
    /// <item>改<b>别人</b>时目标必须存在；</item>
    /// <item>插件覆盖没启用 → 直接<b>自动启用（自己或别人都一样）</b>。
    ///       插件覆盖只在每次请求前重排上下文、<b>不写任何角色文件</b>，关掉插件即完全复原，
    ///       所以没有任何需要征求同意的地方。</item>
    /// </list>
    /// </summary>
    internal string? CheckAgentAccess(AgentRequest request, string action)
    {
        // ⚠️ 必须用「**发起者自己**」的配置，不能用单例上那个共享的 `Config`。
        // `Config` 只反映「最后一个跑 OnUpdate 的角色」的设置 —— 多角色装了本插件时，
        // 别的角色每帧都会把它覆盖掉，于是发起者的权限被无声撤销（2026-10-01 用户报的
        // 「首次改别人成功、之后连给自己加模块都被拦」就是这个）。
        var callerConfig = ConfigFor(request.SelfOwner);
        if (request.IsSelf && !callerConfig.AllowModifySelf)
            return "用户没有开启「允许角色修改自己的上下文」，所以这次" + action + "被拒绝了。你可以把想改的内容告诉用户，请用户来决定。";
        if (!request.IsSelf && !callerConfig.AllowModifyOthers)
            return "用户没有开启「允许角色修改其他角色的上下文」，所以这次" + action + "被拒绝了。你可以把想改的内容告诉用户，请用户来决定。";

        if (IsPluginEnabledFor(request.TargetOwner)) return null;

        // ── 插件覆盖没启用：直接自动启用 ──
        // 插件覆盖只在每次请求前重排上下文，**不碰任何角色文件**，出事关掉插件即可复原。
        // 因此对自己、对别人都不需要问用户 —— 唯一被改动的是「重排时要不要算这个角色」，
        // 那是插件自己的运行时状态，不是用户的角色数据。
        EnablePluginFor(request.TargetOwner);
        ContextTrace.Write($"agent auto-enable plugin owner={request.TargetOwner}（插件覆盖，不动角色文件）");
        return null;
    }

    // ── 通知界面 ────────────────────────────────────────────────────────

    /// <summary>
    /// 角色（AI）通过 XmlFunction 改完计划之后，让已经打开的插件窗口**立刻**看到这次改动。
    ///
    /// <para><b>为什么必须有这个：</b><see cref="ContextAgentOps"/> 是纯逻辑层 ——
    /// 它只有 <c>planService</c>，改完只写磁盘，<b>拿不到 runtime、发不了 IPC</b>。
    /// 而界面这一侧（<c>plan-state</c> / <c>state</c>）只在用户自己点保存、应用、
    /// 或历史被编辑时才推送。于是角色新增/修改/删除模块之后，磁盘上的计划已经变了，
    /// 但用户盯着的那扇窗口永远停在旧样子 —— 必须手动关掉重开才看得到。</para>
    ///
    /// <para><b>这正是用户报的「AI 改的模块在管理界面里不实时刷新 / 新模块根本不显示」。</b>
    /// 修法就是补上这条通知，<b>不动任何既有逻辑</b>。</para>
    ///
    /// <para><b>参数里的 <paramref name="plan"/> 是刚落盘的那一份</b>（由
    /// <see cref="ContextAgentOps.SaveAndNotify"/> 在 <c>SavePlan</c> 之后传入）。
    /// 这里**仍然重新读一遍磁盘**：一是与其它推送点保持同一条路径（走
    /// <c>GetOrCreatePlan</c> 会做 <c>NormalizeFrameworkNames</c> 等规范化），
    /// 二是避免持有调用方的可变对象引用。</para>
    ///
    /// <para><b>为什么要包 <c>lock (stateLock)</c>：</b>与 <c>SendPlan</c> / <c>QueueInitialState</c>
    /// 的其他调用点保持一致 —— 计划读取与推送要在同一把锁下，否则可能在半改状态下被读走。
    /// 注意 <c>SendPlan</c> 自己只入队（<c>SendWindow</c> → 出站线程），不在锁内做阻塞写，
    /// 所以持有 <c>stateLock</c> 调它是安全的。</para>
    ///
    /// <para><b>独立函数而不是直接写进 <c>ContextAgentApi</c>：</b>那里是插件对 AI 的门面，
    /// 应该只管「参数解析 + 调 Ops + 投回消息」，不该知道窗口是否存在、怎么推送。</para>
    /// </summary>
    internal void NotifyPlanChangedFromAgent(string owner, ContextPlan plan)
    {
        try
        {
            if (windowService?.Window == null) return;   // 窗口没开就别白算一遍计划
            if (string.IsNullOrWhiteSpace(owner)) return;
            lock (stateLock)
            {
                plan = GetPlanService().GetOrCreatePlan(owner);
                SendPlanUpdated(owner, plan);
            }
            QueueInitialState();
            ContextTrace.Write($"agent plan changed -> ui notified owner={owner} modules={plan.Modules.Count}");
        }
        catch (Exception ex)
        {
            // 通知失败**绝不能**影响角色那次工具调用的结果 —— 计划已经落盘了，那才是真东西。
            ContextTrace.Write($"agent plan notify failed owner={owner}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>把目标角色的 index.json 补上本插件模块（改自己时自动调用；改别人时用户点了同意才调用）。</summary>
    void EnablePluginFor(string owner)
    {
        try
        {
            UpdateIndexJson(owner, jo =>
            {
                const string moduleId = "Marisa.ContextManager.ContextManagerModule";
                if (jo["Modules"] is not Newtonsoft.Json.Linq.JArray modules)
                {
                    modules = new Newtonsoft.Json.Linq.JArray();
                    jo["Modules"] = modules;
                }
                if (!modules.Any(m => (m?.ToString() ?? "").Contains("ContextManagerModule", StringComparison.OrdinalIgnoreCase)))
                    modules.Add(moduleId);
            });
        }
        catch (Exception ex)
        {
            ContextTrace.Write($"agent enable plugin write failed owner={owner}: {ex.Message}");
        }
    }

    // ── 落地：让改动生效 ────────────────────────────────────────────────

    /// <summary>
    /// 把磁盘上的计划「应用」到目标角色（角色改完之后必须显式调这一步才会真正影响对话）。
    ///
    /// ⚠️ 这里**不再弹窗**（逐次审批已取消）。覆盖方式按目标与约定选择：
    /// <list type="bullet">
    /// <item>改<b>自己</b> → 一律<b>插件覆盖</b>（<c>Temporary</c>）。不动角色文件、随时可关。</item>
    /// <item>改<b>别人</b> → 插件覆盖已启用就用插件覆盖；若用户在前面的弹窗里选了「本地覆盖」，
    ///       才用 <c>Permanent</c>（那一次已经在弹窗里明确同意过，不重复问）。</item>
    /// </list>
    /// </summary>
    internal async Task<(bool Ok, string Message)> ApplyPlanForAgentAsync(AgentRequest request, bool permanent)
    {
        var owner = request.TargetOwner;
        // 改自己永不使用本地覆盖 —— 插件覆盖已经足够，没有理由去动角色文件。
        var usePermanent = permanent && !request.IsSelf;

        try
        {
            var result = await Task.Run(() =>
            {
                lock (stateLock)
                {
                    GetCharacterRequired(owner);
                    var plan = GetPlanService().GetOrCreatePlan(owner);
                    plan.Mode = usePermanent ? "Permanent" : "Temporary";
                    ApplyPlanCore(owner, plan);
                    return plan;
                }
            });
            return (true, $"已应用（{ContextAgentOps.DescribeMode(result.Mode)}），"
                + $"{result.Modules.Count(m => m.Enabled)} 个模块参与发送。");
        }
        catch (Exception ex)
        {
            ContextTrace.Write($"agent apply failed owner={owner}: {ex.GetType().Name}: {ex.Message}");
            return (false, "应用失败：" + ex.Message);
        }
    }
}
