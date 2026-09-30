using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace Marisa.ContextManager;

/// <summary>
/// 一个可渲染的上下文子段。
/// </summary>
public sealed class OverrideSection
{
    public string Key { get; set; } = "";       // character / preset-system / preset-user / preset-ai / card / worldbook
    public string Label { get; set; } = "";     // 角色设定 / 预设·System / ...
    public string Role { get; set; } = "system";// system / user / assistant
    public string Content { get; set; } = "";
}

/// <summary>
/// 上下文覆盖构建器：收集“角色原始设定 + 酒馆预设 + 角色卡字段 + 世界书常驻段”，
/// 执行 Handlebars 扁平宏替换，最终拼到 index[0] 这一层。
/// 第一版只做扁平变量 + 递归替换；{{#if}}/{{#each}} 等块语法原样保留。
/// </summary>
public sealed class ContextOverrideBuilder
{
    // 每段块标记，便于识别/幂等替换/干净移除。
    public const string BlockOpenPrefix = "<!-- marisa-ov:";
    public const string BlockClosePrefix = "<!-- /marisa-ov:";

    readonly Func<string, JObject?> readIndexJson;
    readonly Func<string, string?> readPresetFile;   // presetName -> raw json (null if missing)
    readonly Func<string, JObject?> readWorldBook;   // owner -> WorldBook.json JObject (null if missing)

    public ContextOverrideBuilder(
        Func<string, JObject?> readIndexJson,
        Func<string, string?> readPresetFile,
        Func<string, JObject?> readWorldBook)
    {
        this.readIndexJson = readIndexJson;
        this.readPresetFile = readPresetFile;
        this.readWorldBook = readWorldBook;
    }

    /// <summary>
    /// 构建最终覆盖文本（已含原始角色设定与所有子段，可直接作为 index[0] / Prompt）。
    /// </summary>
    public string Build(string owner, ContextManagerConfig config)
    {
        var index = readIndexJson(owner) ?? new JObject();
        var sections = new List<OverrideSection>();

        // 1) 角色原始设定（始终置顶，保证身份第一）
        var originalPrompt = index.Value<string>("Prompt") ?? "";
        sections.Add(new OverrideSection
        {
            Key = "character",
            Label = "角色设定",
            Role = "system",
            Content = originalPrompt
        });

        // 2) 酒馆预设（按 role 拆 system/user/ai）
        if (!string.IsNullOrWhiteSpace(config.ActivePreset))
        {
            var raw = readPresetFile(config.ActivePreset);
            if (!string.IsNullOrWhiteSpace(raw))
            {
                var preset = JObject.Parse(raw);
                var entries = preset["Entries"] as JArray ?? new JArray();
                var byRole = new Dictionary<string, List<(int order, string name, string content)>>();
                var order = 0;
                foreach (var e in entries.Cast<JObject>())
                {
                    var enabled = e.Value<bool?>("Enabled") ?? true;
                    if (!enabled) continue;
                    var role = NormalizeRole(e.Value<string>("Role") ?? "system");
                    var content = e.Value<string>("Content") ?? "";
                    var name = e.Value<string>("Name") ?? "预设片段";
                    var ord = e.Value<int?>("Order") ?? order;
                    if (!byRole.TryGetValue(role, out var list)) { list = new(); byRole[role] = list; }
                    list.Add((ord, name, content));
                    order++;
                }

                foreach (var role in new[] { "system", "user", "assistant" })
                {
                    if (!byRole.TryGetValue(role, out var list) || list.Count == 0) continue;
                    var label = role == "system" ? "预设·System" : role == "user" ? "预设·User" : "预设·AI";
                    var key = role == "system" ? "preset-system" : role == "user" ? "preset-user" : "preset-ai";
                    var body = string.Join("\n\n", list.OrderBy(x => x.order).Select(x => x.content));
                    sections.Add(new OverrideSection { Key = key, Label = label, Role = role, Content = body });
                }
            }
        }

        // 3) 角色卡字段（描述/性格/场景/系统提示/对话示例/历史后指令）
        if (config.UseCharacterCard)
        {
            var parts = new List<KeyValuePair<string, string>>();
            void Add(string field, string? value)
            {
                if (!string.IsNullOrWhiteSpace(value))
                    parts.Add(new KeyValuePair<string, string>(field, value!.Trim()));
            }
            Add("描述", index.Value<string>("Description"));
            // 角色卡扩展字段（导入时保留），缺失则跳过
            Add("性格", index.Value<string>("Personality"));
            Add("场景", index.Value<string>("Scenario"));
            Add("系统提示", index.Value<string>("SystemPrompt"));
            Add("对话示例", index.Value<string>("MesExample"));
            Add("历史后指令", index.Value<string>("PostHistoryInstructions"));

            if (parts.Count > 0)
            {
                var body = string.Join("\n\n", parts.Select(p => $"【{p.Key}】\n{p.Value}"));
                sections.Add(new OverrideSection { Key = "card", Label = "角色卡", Role = "system", Content = body });
            }
        }

        // 4) 世界书常驻段（enabled + constant；position 为 before / 0）
        if (config.UseWorldBook)
        {
            var book = readWorldBook(owner);
            var entries = book?["Entries"] as JArray;
            if (entries != null)
            {
                var constant = new List<(int order, string title, string content)>();
                foreach (var e in entries.Cast<JObject>())
                {
                    var enabled = e.Value<bool?>("Enabled") ?? true;
                    var isConstant = e.Value<bool?>("Constant") ?? false;
                    if (!enabled || !isConstant) continue;
                    var content = e.Value<string>("Content") ?? "";
                    var title = e.Value<string>("Title") ?? "世界书条目";
                    var order = e.Value<int?>("InsertionOrder") ?? 100;
                    if (!string.IsNullOrWhiteSpace(content))
                        constant.Add((order, title, content));
                }
                if (constant.Count > 0)
                {
                    var body = string.Join("\n\n", constant.OrderBy(x => x.order).Select(x => x.content));
                    sections.Add(new OverrideSection { Key = "worldbook", Label = "世界书", Role = "system", Content = body });
                }
            }
        }

        // 宏替换
        if (config.ApplyMacros)
        {
            var macroEnv = BuildMacroEnv(owner, index);
            foreach (var s in sections)
            {
                if (!string.IsNullOrEmpty(s.Content))
                    s.Content = ApplyMacros(s.Content, macroEnv);
            }
        }

        // 组装：角色设定直接放，其余加带标记块
        var sb = new System.Text.StringBuilder();
        var characterSection = sections.FirstOrDefault(s => s.Key == "character");
        sb.AppendLine((characterSection?.Content ?? "").TrimEnd());

        foreach (var s in sections.Where(s => s.Key != "character"))
        {
            if (string.IsNullOrWhiteSpace(s.Content)) continue;
            sb.AppendLine();
            sb.AppendLine($"{BlockOpenPrefix}{s.Key} role=\"{s.Role}\" label=\"{s.Label}\" -->");
            sb.AppendLine(s.Content.Trim());
            sb.AppendLine($"{BlockClosePrefix}{s.Key} -->");
        }

        return sb.ToString().Trim();
    }

    static string NormalizeRole(string role)
    {
        role = (role ?? "").Trim().ToLowerInvariant();
        if (role == "developer") return "system";
        if (role == "ai" || role == "model") return "assistant";
        if (role == "assistant") return "assistant";
        if (role == "user") return "user";
        return "system";
    }

    Dictionary<string, string> BuildMacroEnv(string owner, JObject index)
    {
        var name = index.Value<string>("Name") ?? owner;
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["char"] = name,
            ["user"] = "User",
            ["description"] = index.Value<string>("Description") ?? "",
            ["personality"] = index.Value<string>("Personality") ?? "",
            ["scenario"] = index.Value<string>("Scenario") ?? "",
            ["charPrompt"] = index.Value<string>("SystemPrompt") ?? "",
            ["charJailbreak"] = "",
            ["charInstruction"] = "",
            ["mesExamples"] = index.Value<string>("MesExample") ?? "",
            ["mesExamplesRaw"] = index.Value<string>("MesExample") ?? "",
            ["postHistoryInstructions"] = index.Value<string>("PostHistoryInstructions") ?? "",
        };
        return env;
    }

    /// <summary>
    /// 扁平 Handlebars 宏替换 + 有限递归（最多 8 轮，防死循环）。
    /// 支持 {{x}} 与 {{ x }}；未知变量保留原样。
    /// </summary>
    static string ApplyMacros(string text, Dictionary<string, string> env)
    {
        var pattern = new Regex(@"\{\{\s*([A-Za-z0-9_]+)\s*\}\}");
        var current = text;
        for (var round = 0; round < 8; round++)
        {
            var changed = false;
            current = pattern.Replace(current, m =>
            {
                var key = m.Groups[1].Value;
                if (env.TryGetValue(key, out var value))
                {
                    changed = true;
                    return value ?? "";
                }
                return m.Value;
            });
            if (!changed) break;
        }
        return current;
    }

    /// <summary>
    /// 从已覆盖文本中移除所有 marisa-ov 注入块，返回原始角色设定部分。
    /// </summary>
    public static string StripOverrideBlocks(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var pattern = new Regex(
            @"\r?\n?\s*<!-- marisa-ov:[^\n]*-->.*?<!-- /marisa-ov:[^\n]*-->",
            RegexOptions.Singleline);
        var stripped = pattern.Replace(text, "");
        return stripped.Trim();
    }
}
