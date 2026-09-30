using System;

namespace Marisa.ContextManager;

/// <summary>
/// 角色预设（快照）的导出 / 导入规则。抽成纯函数以便单元测试。
///
/// 导出：文件名要能看出是谁的方案 —— 自动槽只写角色名，命名槽写「角色__名称」。
/// 导入：等于换主人。文件里记的 owner 可能是别的角色，导入时一律改写成当前角色，
///       并且一定要落进「命名槽」，不能悄悄覆盖当前角色那份自动维护的快照。
/// </summary>
public static class CharacterPresetTransfer
{
    public const string ImportSuffix = "（导入）";

    /// <summary>把一份外部记录绑定到目标角色。</summary>
    public static CharacterContextPreset Rebind(CharacterContextPreset source, string owner, string? fallbackName)
    {
        // 注意：CharacterContextPreset.Plan 有 `= new()` 的字段初始化，
        // 所以「Plan == null」永远不成立 —— 只判断 null 的话，一个没有 plan 字段的文件
        // 会被反序列化成「0 个模块的空计划」，导入后直接顶掉用户的装配方案。
        // 因此这里必须按模块数量判断。
        if (source?.Plan == null || source.Plan.Modules.Count == 0)
            throw new InvalidOperationException("这个文件里没有可识别的装配计划（plan.modules 为空）");

        var name = (source.Name ?? "").Trim();
        if (name.Length == 0) name = (fallbackName ?? "").Trim();
        if (name.Length == 0) name = owner;

        // CharacterPresetPath 把「名称 == 角色名」当作自动快照槽。导入时避开它，
        // 否则一次导入就把那份自动维护的记录覆盖掉了，而且没有任何提示。
        if (string.Equals(name, owner, StringComparison.OrdinalIgnoreCase))
            name = owner + ImportSuffix;

        return new CharacterContextPreset
        {
            Owner = owner,
            Name = name,
            Auto = false,
            UpdatedAt = source.UpdatedAt,
            Plan = source.Plan,
            // 设定来源（酒馆预设 / 世界书 / 酒馆卡）必须跟着走。
            // 漏掉这一行，导入进来的快照就只剩「配方」——模块里的 {{worldbook}} 之类宏
            // 展开成空，用户会以为导入失败。见 CharacterPresetSources 的注释。
            Sources = source.Sources
        };
    }

    /// <summary>导出文件名（不含扩展名部分由 <paramref name="safe"/> 负责清洗）。</summary>
    public static string SuggestedFileName(string owner, string? name, Func<string, string> safe)
    {
        var trimmed = (name ?? "").Trim();
        var stem = trimmed.Length == 0 || string.Equals(trimmed, owner, StringComparison.OrdinalIgnoreCase)
            ? owner
            : owner + "__" + trimmed;
        return safe(stem) + ".contextpreset.json";
    }
}
