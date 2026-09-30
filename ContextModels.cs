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
