using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Marisa.ContextManager;

/// <summary>
/// 上下文装配计划：默认计划、读写、按模块清单渲染（每段标注身份 System/User/Assistant）。
/// </summary>
public sealed class ContextPlanService
{
    public const string BlockOpenPrefix = "<!-- marisa-ov:";
    public const string BlockClosePrefix = "<!-- /marisa-ov:";

    readonly string plansDirectory;
    readonly Func<string, JObject?> readIndexJson;
    readonly Func<JObject?, string, string>? officialPromptBuilder;

    public ContextPlanService(string plansDirectory, Func<string, JObject?> readIndexJson,
        Func<JObject?, string, string>? officialPromptBuilder = null)
    {
        Directory.CreateDirectory(plansDirectory);
        this.plansDirectory = plansDirectory;
        this.readIndexJson = readIndexJson;
        this.officialPromptBuilder = officialPromptBuilder;
    }

    string PlanPath(string owner) => Path.Combine(plansDirectory, SafeFileName(owner) + ".json");

    static readonly JsonSerializerSettings JsonSettings = new()
    {
        Formatting = Formatting.Indented
    };

    /// <summary>
    /// 读取计划；不存在则用角色 index.json 的 Prompt 生成默认计划（含一个“角色设定”System 模块）。
    /// </summary>
    public ContextPlan GetOrCreatePlan(string owner)
    {
        var path = PlanPath(owner);
        if (File.Exists(path))
        {
            var plan = JsonConvert.DeserializeObject<ContextPlan>(File.ReadAllText(path)) ?? new ContextPlan();
            if (plan.Modules == null) plan.Modules = new List<ContextPlanModule>();
            ContextPromptText.NormalizeFrameworkNames(plan.Modules);
            return plan;
        }
        return readIndexJson(owner)?["ContextManagerPlan"]?.ToObject<ContextPlan>() ?? CreateDefaultPlan(owner);
    }

    public ContextPlan CreateDefaultPlan(string owner)
    {
        var index = readIndexJson(owner);
        var prompt = index?.Value<string>("Prompt") ?? "";
        // 「角色设定」模块保存的是**完整的官方系统消息**（名称/生日/简介/设定/私人文件夹）。
        // 这样在「插件覆盖」下用户可以连同框架注入的那段人物信息一起改写；
        // 本地覆盖写回 index.json 时会再把外壳剥掉，避免出现两份人物信息。
        var content = "";
        try { content = officialPromptBuilder?.Invoke(index, owner) ?? ""; }
        catch { content = ""; }
        if (string.IsNullOrWhiteSpace(content)) content = prompt;
        return new ContextPlan
        {
            Mode = "Off",
            ApplyMacros = true,
            UpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            Modules = new List<ContextPlanModule>
            {
                new ContextPlanModule
                {
                    Name = "角色设定 #0",
                    Source = "native",
                    Role = "System",
                    Content = content,
                    Enabled = true
                }
            }
        };
    }

    public void SavePlan(string owner, ContextPlan plan)
    {
        ContextCompiler.Validate(plan);
        plan.UpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        var path = PlanPath(owner);
        File.WriteAllText(path + ".tmp", JsonConvert.SerializeObject(plan, JsonSettings));
        File.Move(path + ".tmp", path, true);
    }

    public bool PlanExists(string owner) => File.Exists(PlanPath(owner));

    public void DeletePlan(string owner)
    {
        var path = PlanPath(owner);
        if (File.Exists(path)) File.Delete(path);
    }

    /// <summary>
    /// 把计划渲染为最终写进 [0] 的文本。
    /// 第一个“角色设定”模块裸放（保证可被剥离还原），其余模块按身份分块。
    /// </summary>
    public string Render(string owner, ContextPlan plan)
    {
        var index = readIndexJson(owner);
        var env = BuildMacroEnv(owner, index);

        var enabled = plan.Modules.Where(m => m.Enabled).ToList();
        var sb = new System.Text.StringBuilder();

        // 第一段：角色设定（裸）
        var first = enabled.FirstOrDefault();
        if (first != null)
        {
            var body = plan.ApplyMacros ? ApplyMacros(first.Content ?? "", env) : (first.Content ?? "");
            sb.AppendLine(body.TrimEnd());
        }

        // 其余：分块，标注身份
        foreach (var module in enabled.Skip(1))
        {
            var body = plan.ApplyMacros ? ApplyMacros(module.Content ?? "", env) : (module.Content ?? "");
            if (string.IsNullOrWhiteSpace(body)) continue;
            sb.AppendLine();
            sb.AppendLine($"{BlockOpenPrefix}{module.Id} role=\"{NormalizeRoleForTag(module.Role)}\" name=\"{EscapeAttr(module.Name)}\" -->");
            sb.AppendLine(body.Trim());
            sb.AppendLine($"{BlockClosePrefix}{module.Id} -->");
        }

        return sb.ToString().Trim();
    }

    static string EscapeAttr(string s)
        => (s ?? "").Replace("&", "&amp;").Replace("\"", "&quot;");

    static string NormalizeRoleForTag(string role)
    {
        role = (role ?? "").Trim().ToLowerInvariant();
        if (role == "user") return "User";
        if (role == "assistant" || role == "ai" || role == "model") return "Assistant";
        return "System";
    }

    Dictionary<string, string> BuildMacroEnv(string owner, JObject? index)
    {
        var name = index?.Value<string>("Name") ?? owner;
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["char"] = name,
            ["user"] = "User",
            ["description"] = index?.Value<string>("Description") ?? "",
            ["personality"] = index?.Value<string>("Personality") ?? "",
            ["scenario"] = index?.Value<string>("Scenario") ?? "",
            ["charPrompt"] = index?.Value<string>("SystemPrompt") ?? "",
            ["mesExamples"] = index?.Value<string>("MesExample") ?? "",
            ["postHistoryInstructions"] = index?.Value<string>("PostHistoryInstructions") ?? ""
        };
    }

    static string ApplyMacros(string text, Dictionary<string, string> env)
    {
        var pattern = new Regex(@"\{\{\s*([A-Za-z0-9_]+)\s*\}\}");
        var current = text;
        for (var round = 0; round < 8; round++)
        {
            var changed = false;
            current = pattern.Replace(current, m =>
            {
                if (env.TryGetValue(m.Groups[1].Value, out var value))
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

    /// <summary>移除所有注入块，还原为裸的角色设定。</summary>
    public static string StripBlocks(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var pattern = new Regex(
            @"\r?\n?\s*<!-- marisa-ov:[^\n]*-->.*?<!-- /marisa-ov:[^\n]*-->",
            RegexOptions.Singleline);
        return pattern.Replace(text, "").Trim();
    }

    static string SafeFileName(string name)
    {
        name = (name ?? "").Trim();
        if (string.IsNullOrEmpty(name)) name = "plan";
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name;
    }
}
