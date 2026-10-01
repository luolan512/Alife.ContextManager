using System;
using System.Collections.Generic;

namespace Marisa.ContextManager;

public sealed class ContextAttachment
{
    public string Modality { get; set; } = "file";
    public string Status { get; set; } = "ok";
    public string Title { get; set; } = "";
    public string Path { get; set; } = "";
    public string Detail { get; set; } = "";
    public int Size { get; set; }
}

public sealed class ContextItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Category { get; set; } = "";
    public string Kind { get; set; } = "text";
    public string Modality { get; set; } = "text";
    public string Owner { get; set; } = "";
    public string Title { get; set; } = "";
    public string Content { get; set; } = "";
    public string Path { get; set; } = "";
    public string Source { get; set; } = "";
    public string SourceKey { get; set; } = "";
    public string Origin { get; set; } = "live";
    /// <summary>
    /// 这条实时消息的**身份指纹** —— 用来在对话历史发生变化（前部被裁剪、中间插入新消息）之后，
    /// 依然能认出「装配计划里的那个模块」对应的是哪一条消息。
    ///
    /// <para>
    /// 为什么需要它：装配计划里的 framework 模块以前只存 <see cref="ContextPlanModule.TargetIndex"/>
    /// （消息在 <c>ChatBot.ChatHistory</c> 数组里的下标）。下标不是身份 —— 历史一变，
    /// 原来第 3 条就成了第 2 条，而计划里记的还是 3，于是界面上「模块名字」与「模块内容」
    /// 配到了两条不同的消息，看起来就是错位。
    /// </para>
    /// <para>
    /// 只有来自实时对话历史（<c>live-system</c> / <c>live-history</c>）的条目才有值；
    /// 磁盘上的条目（记忆、角色卡等）本来就是按路径 / Id 定位的，不需要它。
    /// 格式：<c>来源|角色|函数说明名或正文指纹</c>，由 <c>ContextManagerRuntime.ComputeAnchorKey</c> 生成。
    /// </para>
    /// </summary>
    public string AnchorKey { get; set; } = "";
    /// <summary>
    /// 这条实时条目在 <c>ChatBot.ChatHistory</c> 里的**真实次序**（仅 live-system / live-history 有值，其余为 -1）。
    ///
    /// <para>
    /// 为什么不用 <see cref="Title"/> 里的 <c>#N</c> 排序：那个 <c>#N</c> 也是历史下标，
    /// 但它只是**字符串**的一部分 —— 前端要用正则去抠，一旦标题格式变了（例如某类消息换了个命名）
    /// 就会抠错或抠不到，退化成 <c>-1</c>，排序随之乱掉。更要命的是前端拿到的 item 可能来自
    /// 不同批次（部分走 <c>items:page</c> 补全），靠解析标题很容易出现「同一批里有的有 #、有的没有」。
    /// </para>
    /// <para>
    /// 这里直接由后端在采集时赋一个**明确的整数**，前端排它即可，不需要解析字符串。
    /// 它同样是历史下标、同样会随历史变动而变 —— 但它的语义是「**本次快照里这条消息排第几**」，
    /// 只用于**显示排序**，绝不用于身份配对（配对一律走 <see cref="AnchorKey"/>）。
    /// </para>
    /// </summary>
    public int LiveOrder { get; set; } = -1;
    public long Size { get; set; }
    public int EstimatedTokens { get; set; }
    public bool Enabled { get; set; } = true;
    public bool ReadOnly { get; set; } = true;
    public int MemoryLevel { get; set; }
    public List<string> Tags { get; set; } = new();
    public List<ContextAttachment> Attachments { get; set; } = new();
    public DateTime? CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    /// <summary>
    /// 这条条目的 Content 是预览、不是全文（由 ContextStateBudget 在报文超预算时设置）。
    /// 渲染进程在把内容写回去之前必须先 item:load 取回全文，否则会把预览当成正文存盘。
    /// </summary>
    public bool Truncated { get; set; }
}

public sealed class MemoryLevelInfo
{
    public int Level { get; set; }
    public int Count { get; set; }
    public int Tokens { get; set; }
}

public sealed class ContextSnapshot
{
    public List<ContextItem> Items { get; set; } = new();
    public List<OwnerSummary> Owners { get; set; } = new();
    public List<CategorySummary> Categories { get; set; } = new();
    public List<ModalitySummary> Modalities { get; set; } = new();
    public List<MemoryLevelInfo> MemoryLevels { get; set; } = new();
    public DiagnosticsInfo Diagnostics { get; set; } = new();
    /// <summary>
    /// 为了把报文压进预算而被「不列出」的条目数（ContextStateBudget 的最后一道兜底）。
    /// 界面用它说明「还有 N 条没显示」，而不是让用户以为上下文只有这些。
    /// </summary>
    public int HiddenItems { get; set; }

    /// <summary>
    /// 本次 state 里没有包含、需要走 items:page 按需分页取的条目数（全局合计）。
    /// 与 HiddenItems 的区别：HiddenItems 是「超预算被迫丢掉」，
    /// OmittedItems 是「按分页策略主动延后加载」——后者是常态，不是异常。
    /// </summary>
    public int OmittedItems { get; set; }

    /// <summary>
    /// 同上，但**按角色拆开**。界面上的分页条说的是「当前这个角色还有 N 条未加载」，
    /// 直接用全局 OmittedItems 会把别的角色的欠账算到当前角色头上，所以必须分开统计。
    /// </summary>
    public Dictionary<string, int> OmittedByOwner { get; set; } = new();
    // Serialized plans deliberately travel as strings: the initial state channel is
    // reliable in Electron.NET whereas nested DTO replies have proven unreliable.
    public Dictionary<string, string> PlanJsonByOwner { get; set; } = new();
    // Exact ChatHistory[0] text of active roles, for showing the framework-built
    // system message without confusing it with the editable Character.Prompt.
    public Dictionary<string, string> RuntimeSystemByOwner { get; set; } = new();
    // Editable index[0] source for the active character, even before the
    // ChatActivity has rebuilt its runtime system message.
    public Dictionary<string, string> CharacterPromptByOwner { get; set; } = new();
    // 报文超预算时，ContextStateBudget 会按顺序丢掉这些大字段并记录丢过什么。
    // 渲染进程据此提示用户「详情按需加载」，而不是默默显示一份不完整的数据。
    public bool Degraded { get; set; }
    public List<string> DegradeSteps { get; set; } = new();
}

public sealed class OwnerSummary
{
    public string Name { get; set; } = "";
    public int Count { get; set; }
    public int Tokens { get; set; }
    public bool Active { get; set; }
    public bool MemoryEnabled { get; set; }
}

public sealed class CategorySummary
{
    public string Name { get; set; } = "";
    public int Count { get; set; }
    public int Tokens { get; set; }
}

public sealed class ModalitySummary
{
    public string Name { get; set; } = "";
    public int Count { get; set; }
    public int Tokens { get; set; }
}

public sealed class DiagnosticsInfo
{
    public string BaseDirectory { get; set; } = "";
    public string StorageRoot { get; set; } = "";
    public string GeneratedAt { get; set; } = "";
    // 后端期望的前端构建号。渲染进程用它判断自己是不是一份旧缓存的 app.js。
    public string AppBuild { get; set; } = "";
    public int TotalItems { get; set; }
    public int TotalTokens { get; set; }
}
