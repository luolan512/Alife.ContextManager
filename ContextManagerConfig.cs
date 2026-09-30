namespace Marisa.ContextManager;

/// <summary>
/// 框架「插件配置」面板里这一组开关（Alife 按 <c>IConfigurable&lt;T&gt;</c> 自动生成界面）。
///
/// ⚠️ 这里**只保留真的有人读的字段**。
///
/// 2026-09-30 清理：历史上这个类还挂着
/// <c>IncludeMemoryHistory</c> / <c>IncludeNotes</c> / <c>IncludeSkills</c> /
/// <c>IncludeGroupChat</c> / <c>MaxHistoryItems</c> / <c>MaxPreviewChars</c> 六个字段，
/// 但全项目（含前端）**没有任何一行代码读它们** —— 面板上却显示得像真开关，
/// 用户改完发现没反应。其中 <c>MaxPreviewChars</c> 造成的误解最深：旧代码曾
/// 「临时把它改成 220 再取摘要」，看起来像在做短预览，实际上取内容的一直是
/// <c>Limit()</c>（无条件返回全文），state 报文一直在推全文 ——
/// 这正是 2026-09-29「激活第二个角色把 IPC 桥顶断」的成因之一。
/// 删掉这些字段，面板上剩下的每一个都是真的生效的。
/// </summary>
public class ContextManagerConfig
{
    /// <summary>
    /// 上下文覆盖方式。
    /// <c>Off</c> = 关闭（走 Alife 原生装配）；
    /// <c>Temporary</c> = 插件覆盖（只在每次请求前重排上下文，不动角色文件）；
    /// <c>Permanent</c> = 本地覆盖（把装配结果写进角色 index.json，插件关掉也生效）。
    /// 与插件窗口里「上下文装配」页的「覆盖方式」是同一个值，两边会同步。
    /// </summary>
    public string OverrideMode { get; set; } = "Off";

    /// <summary>当前选中的酒馆预设名（对应 <c>Storage/ContextManager/Presets/&lt;名&gt;.json</c>）。</summary>
    public string ActivePreset { get; set; } = "";

    /// <summary>
    /// 是否把角色卡字段（描述 / 性格 / 场景 / 对话示例 / 系统指令 / 历史后指令）
    /// 并入 <c>index[0]</c> 层。见 <see cref="ContextOverrideBuilder"/>。
    /// </summary>
    public bool UseCharacterCard { get; set; } = true;

    /// <summary>是否把世界书常驻段（before 位置）并入 <c>index[0]</c> 层。</summary>
    public bool UseWorldBook { get; set; } = true;

    /// <summary>
    /// 渲染时是否执行 Handlebars 宏替换。关掉就按原文发送
    /// （此时 <c>{{char}}</c> 之类不会被替换）。与装配页控制栏的「启用 Handlebars 宏」是同一个值。
    /// </summary>
    public bool ApplyMacros { get; set; } = true;
}
