using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Marisa.ContextManager;

/// <summary>
/// 一个可编辑的上下文模块（类似酒馆预设里的一条提示词）。
/// </summary>
public sealed class ContextPlanModule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    // System / User / Assistant —— 该模块以什么“身份”发送
    public string Role { get; set; } = "System";
    public string Content { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public string Group { get; set; } = "system";
    public string Source { get; set; } = "custom";
    public bool Constant { get; set; } = true;
    public List<string> Keywords { get; set; } = new();
    public List<string> SecondaryKeywords { get; set; } = new();
    public bool Selective { get; set; }
    public bool CaseSensitive { get; set; }
    public int TargetIndex { get; set; } = -1;
    /// <summary>
    /// 这个 framework 模块锚定的那条实时消息的**身份指纹**（见 <see cref="ContextItem.AnchorKey"/>）。
    ///
    /// <para>
    /// <see cref="TargetIndex"/> 是「它在 ChatHistory 数组里的第几位」——会随历史裁剪而失效；
    /// 本字段是「它是哪一条消息」——不会。装配时优先用它找消息，找不到才退回 <see cref="TargetIndex"/>。
    /// 空字符串表示这是一个老的、还没锚定过的模块（首次匹配成功时会自动补上并落盘）。
    /// </para>
    /// </summary>
    public string AnchorKey { get; set; } = "";
    public string OriginalContent { get; set; } = "";
    public string OriginalRole { get; set; } = "system";

    /// <summary>
    /// 本条模块的正文**没有随报文下发**（被截成了预览），只是为了让装配页能渲染出来。
    ///
    /// 为什么需要它：一张 142 条世界书的角色卡，导入后计划有 143 个模块、正文 9.9 万字，
    /// 序列化后超过单条 IPC 报文的预算（见 ContextStateBudget）。以前 SendPlan 直接拒发，
    /// 用户看到的是「导入完成」但装配页什么都没有。现在改成按预算下发骨架 + 标记，
    /// 后端在 save / preview / apply 时按 <see cref="Id"/> 从磁盘上的计划里把正文回填
    /// （见 ContextAssemblyRuntime.PreparePlan），所以前端不需要持有全文，也不会丢数据。
    ///
    /// ⚠️ **绝不能落盘**。PreparePlan 在回填后会把它清成 false，落盘的计划永远是全文。
    /// </summary>
    public bool Truncated { get; set; }

    /// <summary>正文的真实长度（字符数）。仅在 <see cref="Truncated"/> 为 true 时有意义，用来显示「正文未下发（N 字）」。</summary>
    public int ContentLength { get; set; }
}

/// <summary>
/// 一份上下文装配计划：模式 + 模块清单。
/// </summary>
public sealed class ContextPlan
{
    // Off / Temporary / Permanent
    public string Mode { get; set; } = "Off";
    public List<ContextPlanModule> Modules { get; set; } = new();
    public bool ApplyMacros { get; set; } = true;

    /// <summary>
    /// 「自动宏应用」：遇到本插件没有实现、用户也没定义的宏时，直接把它替换成宏名本身，
    /// 而不是原样保留 <c>{{lastPrompt}}</c>。
    ///
    /// <para>为什么要有它：本插件没有酒馆那么丰富的宏能力（没有 <c>lastPrompt</c>、
    /// <c>random</c>、<c>time</c> 这些需要运行时状态的宏）。以前这些宏会原样留在提示词里，
    /// 并在预览里堆成一长串「未识别的宏」警告，用户只能一条条手工填进「自定义宏」。
    /// 打开这个开关后：<c>{{char}}</c> 仍然取角色名、<c>{{user}}</c> 取上面填的名字，
    /// 其余未定义的宏（如 <c>{{lastPrompt}}</c>）就直接变成它自己的名字。
    /// 想恢复「原样保留 + 提示」，关掉即可。</para>
    /// </summary>
    public bool AutoMacros { get; set; } = true;

    public string UpdatedAt { get; set; } = "";
    // {{user}} 的取值；界面可编辑。
    public string UserName { get; set; } = "User";
    // 用户自定义宏：键为宏名（不含大括号），值为替换文本。会覆盖同名内置宏。
    public Dictionary<string, string> CustomMacros { get; set; } = new();
    public int ScanDepth { get; set; } = 10;
}

/// <summary>
/// 「角色预设」：某一个角色的一份上下文记录快照。
/// 每个角色都会自动保留一份（Auto=true，名字即角色名）；用户还可以另外存多份命名快照。
/// 存放位置：Storage/ContextManager/CharacterPresets/&lt;角色&gt;.json 与 &lt;角色&gt;__&lt;名称&gt;.json。
/// </summary>
public sealed class CharacterContextPreset
{
    public string Owner { get; set; } = "";
    public string Name { get; set; } = "";
    // true = 插件自动维护的同名快照；false = 用户手动保存的命名快照。
    public bool Auto { get; set; }
    public string UpdatedAt { get; set; } = "";
    public ContextPlan Plan { get; set; } = new();

    /// <summary>
    /// 快照里一并保存的「设定来源」：酒馆预设、卡内世界书、酒馆角色卡。
    ///
    /// 为什么要有它：快照以前只存装配计划（模块清单），而模块里的正文大多是**宏**
    /// （{{description}}、{{worldbook}}…），宏的取值来自角色目录下的 TavernCard.json /
    /// WorldBook.json 与 ContextManager/Presets/&lt;名&gt;.json。只带走 .contextpreset.json
    /// 等于只带走一张「配方」，原料还留在原角色目录里 —— 换角色、换机器就散架。
    /// 现在把原料一起打包，一份文件就是完整的一套设定。
    /// </summary>
    public CharacterPresetSources? Sources { get; set; }
}

/// <summary>
/// 角色快照里的「设定来源」原文。全部按原文件内容存，不做改写，
/// 这样导出 / 导入往返一次仍然逐字相同（可单元测试断言）。
/// </summary>
public sealed class CharacterPresetSources
{
    public string CapturedAt { get; set; } = "";
    /// <summary>抓取时选中的酒馆预设名（对应 ContextManager/Presets/&lt;名&gt;.json）。</summary>
    public string ActivePreset { get; set; } = "";
    /// <summary>酒馆预设原文（prompts / prompt_order / Entries）。</summary>
    public JObject? Preset { get; set; }
    /// <summary>角色目录下 WorldBook.json 的原文。</summary>
    public JObject? WorldBook { get; set; }
    /// <summary>角色目录下 TavernCard.json 的原文（酒馆卡的 data 段）。</summary>
    public JObject? TavernCard { get; set; }

    /// <summary>
    /// 抓取时各源文件的指纹（"文件名|长度|最后写入时间"）。自动快照在每次保存方案时都会重抓一次，
    /// 指纹没变就复用上一次的结果 —— 否则每勾选一个模块都要重新解析一份 1 MB 的角色卡。
    /// </summary>
    public List<string> Fingerprints { get; set; } = new();

    public bool IsEmpty
        => Preset == null && WorldBook == null && TavernCard == null && string.IsNullOrWhiteSpace(ActivePreset);

    /// <summary>世界书条目数（按两种形状都能读的 TavernImport.EntryRows 数）。</summary>
    public int WorldBookEntries => WorldBook == null ? 0 : TavernImport.EntryRows(WorldBook).Count();
}

/// <summary>角色预设列表项（下发给界面）。</summary>
public sealed class CharacterPresetInfo
{
    public string Name { get; set; } = "";
    public bool Auto { get; set; }
    public string UpdatedAt { get; set; } = "";
    public string Mode { get; set; } = "Off";
    public int Modules { get; set; }
    // 快照里到底带没带设定来源 —— 界面要能一眼看出「这份记录是完整的」还是「只有配方」。
    public int WorldbookEntries { get; set; }
    public bool HasTavernCard { get; set; }
    public string ActivePreset { get; set; } = "";
}
