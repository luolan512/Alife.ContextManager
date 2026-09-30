using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Newtonsoft.Json.Linq;

namespace Marisa.ContextManager;

// Compile a copy for the request only. Never put prompt User/Assistant messages in MemoryService's history.
public static class ContextCompiler
{
    public static readonly string[] Groups = { "system", "features", "memory", "chat" };
    public static string Role(string? role) => (role ?? "system").ToLowerInvariant() switch {
        "user" => "user", "assistant" or "ai" or "model" => "assistant", _ => "system"
    };
    public static void Validate(ContextPlan plan)
    {
        if (plan.Mode is not ("Off" or "Temporary" or "Permanent")) throw new InvalidOperationException("无效的覆盖模式");
        plan.Modules ??= new();
        var ids = new HashSet<string>();
        foreach (var m in plan.Modules) {
            if (string.IsNullOrWhiteSpace(m.Id) || !ids.Add(m.Id)) throw new InvalidOperationException("模块 ID 为空或重复");
            if (!Groups.Contains(m.Group)) throw new InvalidOperationException("无效的模块区域：" + m.Group);
            m.Role = Role(m.Role); m.Content ??= ""; m.Keywords ??= new(); m.SecondaryKeywords ??= new();
        }
        plan.ScanDepth = Math.Clamp(plan.ScanDepth, 1, 1000);
    }
    /// <param name="warnings">
    /// 可选：收集“降级处理”的说明。运行时装配（ContextTransform）会在每次请求时执行，
    /// 一旦抛异常就会把整轮对话打断，所以这里对“原生模块已变化”这类可恢复情况改为
    /// 保留原始消息并记一条 warning，而不是 throw。结构性错误（模板块不闭合等）仍然抛。
    /// </param>
    public static ChatHistory Compile(ChatHistory source, ContextPlan plan, JObject index, string owner, List<string>? warnings = null)
    {
        Validate(plan);
        if (plan.Mode == "Off") return source;
        var result = new ChatHistory();
        var scan = string.Join("\n", source.Where(m => m.Role != AuthorRole.System).TakeLast(plan.ScanDepth).Select(m => m.Content));
        var env = Environment(index, owner, plan.UserName, plan.CustomMacros);
        var vars = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        void Warn(string text) { if (warnings != null && !warnings.Contains(text)) warnings.Add(text); }
        // 「自动宏应用」：未定义的宏回填宏名本身（见 ContextPlan.AutoMacros）。
        var autoMacros = plan.AutoMacros;
        // 原生消息被框架改写后，旧计划里的 OriginalContent 就对不上了。以前这里直接 throw，
        // 结果是在原生对话窗口每发一条消息都报错；现在退回原始消息，保证对话可用。
        bool Stale(ContextPlanModule m, ChatMessageContent original) =>
            original.Content != m.OriginalContent || original.Role.Label != m.OriginalRole;
        void AddSource(int position) {
            var original = source[position];
            var edit = plan.Modules.FirstOrDefault(m=>m.Source=="framework" && m.TargetIndex==position);
            var sourceGroup = position > 0 && original.Role == AuthorRole.System ? "features" : "chat";
            if (edit == null) { result.Add(original); return; }
            if (Stale(edit, original)) { Warn("原生模块已变化，已保留原文：" + edit.Name); result.Add(original); return; }
            if (edit.Group != sourceGroup) return;
            if (edit.Enabled) result.Add(new ChatMessageContent(new AuthorRole(Role(edit.Role)), plan.ApplyMacros ? Render(edit.Content,env,vars, null, autoMacros) : edit.Content));
        }
        void Add(string group) {
            foreach (var m in plan.Modules.Where(m => m.Group == group && m.Enabled && (m.Source != "framework" || IsMovedFrameworkModule(m, group)))) {
                if (m.Source == "framework")
                {
                    if (m.TargetIndex < 0 || m.TargetIndex >= source.Count) { Warn("原生模块位置已越界，已跳过：" + m.Name); continue; }
                    var original = source[m.TargetIndex];
                    if (Stale(m, original)) { Warn("原生模块已变化，已跳过替换：" + m.Name); continue; }
                    result.Add(new ChatMessageContent(new AuthorRole(Role(m.Role)), plan.ApplyMacros ? Render(m.Content,env,vars, null, autoMacros) : m.Content));
                    continue;
                }
                if (m.Source == "worldbook" && !m.Constant) {
                    var comparison = m.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
                    bool Match(string k) => !string.IsNullOrWhiteSpace(k) && scan.Contains(k, comparison);
                    if (!m.Keywords.Any(Match) || (m.Selective && !m.SecondaryKeywords.Any(Match))) continue;
                }
                var text = plan.ApplyMacros ? Render(m.Content, env, vars, null, autoMacros) : m.Content;
                if (!string.IsNullOrWhiteSpace(text)) result.Add(new ChatMessageContent(new AuthorRole(Role(m.Role)), text));
            }
        }
        bool IsMovedFrameworkModule(ContextPlanModule m, string group)
        {
            if (m.TargetIndex < 0 || m.TargetIndex >= source.Count) return false;
            var original = source[m.TargetIndex];
            var sourceGroup = m.TargetIndex > 0 && original.Role == AuthorRole.System ? "features" : "chat";
            return sourceGroup != group;
        }
        Add("system");
        // index[0] is the replaced region; all framework messages after it remain intact.
        int cursor = source.Count > 0 ? 1 : 0;
        while (cursor < source.Count && source[cursor].Role == AuthorRole.System) AddSource(cursor++);
        Add("features");
        while (cursor < source.Count && (source[cursor].Content ?? "").StartsWith("[记忆存档(")) AddSource(cursor++);
        Add("memory");
        while (cursor < source.Count) AddSource(cursor++);
        Add("chat");
        return result;
    }
    public static Dictionary<string,string> Environment(JObject index, string owner, string user, IDictionary<string,string>? custom = null)
    {
        var card = index["TavernCard"] as JObject ?? new JObject();
        string Field(string legacy, string key) => card.Value<string>(key) ?? index.Value<string>(legacy) ?? "";
        var env = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase) {
            ["char"] = index.Value<string>("Name") ?? owner, ["user"] = user,
            ["description"] = Field("Description","description"), ["personality"] = Field("Personality","personality"),
            ["scenario"] = Field("Scenario","scenario"), ["system"] = Field("SystemPrompt","system_prompt"),
            ["mesExamples"] = Field("MesExample","mes_example"), ["postHistoryInstructions"] = Field("PostHistoryInstructions","post_history_instructions"),
            ["charCreatorNotes"] = Field("CreatorNotes","creator_notes"), ["greeting"] = Field("FirstMes","first_mes"),
            ["newline"] = "\n", ["noop"] = "", ["original"] = ""
        };
        env["charPrompt"] = env["systemPrompt"] = env["system"];
        env["mesExamplesRaw"] = env["mesExamples"];
        env["charDescription"] = env["description"]; env["charPersonality"] = env["personality"]; env["charScenario"] = env["scenario"];
        // 角色卡的开场白可能不止一条（first_mes + alternate_greetings）。
        // 第一条仍然是 {{greeting}}；其余按顺序映射成 {{greeting2}}、{{greeting3}}…，
        // 这样「多条开场白」既能各自成模块，也能被预设用宏引用。
        var greetings = TavernImport.Greetings(card);
        for (var i = 1; i < greetings.Count; i++) env["greeting" + (i + 1)] = greetings[i].Text;
        env["alternateGreetings"] = string.Join("\n\n", greetings.Skip(1).Select(g => g.Text));
        env["greetingCount"] = greetings.Count.ToString();
        // 用户自定义宏放在最后，可以覆盖同名内置宏。
        if (custom != null)
            foreach (var pair in custom)
                if (!string.IsNullOrWhiteSpace(pair.Key)) env[pair.Key.Trim()] = pair.Value ?? "";
        return env;
    }
    // Supported Handlebars subset with nested if/unless and ST flat macros.
    // 未识别的宏不再中断整个请求：酒馆预设/角色卡里常见 {{//注释}}、{{comment}} 或提供方
    // 专有宏，直接抛错会让“应用/预览”看起来像点了没反应。默认原样保留，并把宏名
    // 收集到 unresolved，由界面提示，用户可自行决定是否处理。
    //
    // autoFillNames（「自动宏应用」，见 ContextPlan.AutoMacros）为 true 时改为
    // **回填宏名本身**：{{lastPrompt}} → lastPrompt。带参数的宏只取参数前的名字
    // （{{random::a::b}} → random），免得把一串参数当成正文写进提示词。
    public static string Render(string text, Dictionary<string,string> env, Dictionary<string,string>? variables = null, List<string>? unresolved = null, bool autoFillNames = false)
    {
        variables ??= new(StringComparer.OrdinalIgnoreCase);
        for (int pass = 0; pass < 8; pass++) {
            var before = text;
            text = Blocks(text, env);
            text = Regex.Replace(text, @"\{\{\{?\s*([^{}]+?)\s*\}\}\}?", m => {
                string key = m.Groups[1].Value.Trim();
                // {{!...}} 与酒馆的 {{//...}} 都是注释，直接删除。
                if (key.StartsWith("!") || key.StartsWith("//")) return "";
                if (key.StartsWith("setvar::", StringComparison.OrdinalIgnoreCase)) {
                    var parts = key.Split(new[]{"::"}, 3, StringSplitOptions.None);
                    if (parts.Length != 3) throw new InvalidOperationException("无效宏：" + m.Value);
                    variables[parts[1]] = parts[2]; return "";
                }
                if (key.StartsWith("getvar::", StringComparison.OrdinalIgnoreCase)) return variables.GetValueOrDefault(key[8..], "");
                if (key == "trim") return m.Value;
                if (env.TryGetValue(key, out var value)) return value;
                if (autoFillNames) {
                    // 自动宏应用：填宏名本身。带参数的宏只保留名字部分。
                    var name = key.Split(new[]{"::"}, 2, StringSplitOptions.None)[0].Trim();
                    return name.Length > 0 ? name : "";
                }
                if (unresolved != null && !unresolved.Contains(key)) unresolved.Add(key);
                return m.Value;
            });
            text = Regex.Replace(text, @"\s*\{\{trim\}\}\s*", "");
            if (text.Length > 4_000_000) throw new InvalidOperationException("模板展开过大");
            if (text == before) break;
        }
        return text;
    }
    static string Blocks(string text, Dictionary<string,string> env)
    {
        var tokens = Regex.Matches(text, @"\{\{\s*(#(?:if|unless)\s+[^{}]+|else|/(?:if|unless))\s*\}\}");
        var stack = new Stack<(Match open, Match? alt)>();
        foreach (Match token in tokens) {
            string tag = token.Groups[1].Value.Trim();
            if (tag.StartsWith("#")) stack.Push((token, null));
            else if (tag == "else") {
                if (stack.Count == 0) throw new InvalidOperationException("模板 else 缺少开始块");
                var top = stack.Pop(); stack.Push((top.open, token));
            } else {
                if (stack.Count == 0) throw new InvalidOperationException("模板关闭块缺少开始块");
                var top = stack.Pop();
                var parts = top.open.Groups[1].Value.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (tag[1..] != parts[0][1..]) throw new InvalidOperationException("模板块不匹配");
                bool truth = env.TryGetValue(parts[1].Trim(), out var value) && !string.IsNullOrEmpty(value) && value != "false";
                if (parts[0] == "#unless") truth = !truth;
                int start = top.open.Index + top.open.Length;
                string body = truth ? text[start..(top.alt?.Index ?? token.Index)] : top.alt == null ? "" : text[(top.alt.Index + top.alt.Length)..token.Index];
                return Blocks(text[..top.open.Index] + body + text[(token.Index + token.Length)..], env);
            }
        }
        if (stack.Count > 0) throw new InvalidOperationException("模板块未关闭");
        return text;
    }
}
