using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Marisa.ContextManager;
public static class TavernImport
{
    /// <summary>
    /// 读一张酒馆角色卡。支持 .png（tEXt 块 chara / ccv3）与 .json。
    ///
    /// 关于 chara 与 ccv3：两张块里的 JSON **通常内容相同**（ccv3 是 v3 规范版本），
    /// 但实测也存在同长度而内容不同的情况。这里固定优先返回 ccv3 —— 只返回一份，
    /// 调用方就不会把同一份设定导入两遍。
    /// </summary>
    public static JObject ReadCard(string path)
    {
        if (!Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase)) return JObject.Parse(File.ReadAllText(path));
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        if (!reader.ReadBytes(8).SequenceEqual(new byte[]{137,80,78,71,13,10,26,10})) throw new InvalidDataException("无效 PNG");
        JObject? card = null;
        while (stream.Position + 12 <= stream.Length) {
            var lengthBytes = reader.ReadBytes(4); Array.Reverse(lengthBytes);
            uint length = BitConverter.ToUInt32(lengthBytes);
            var type = Encoding.ASCII.GetString(reader.ReadBytes(4));
            if (length > 32_000_000 || stream.Position + length + 4 > stream.Length) throw new InvalidDataException("PNG 块过大或损坏");
            var data = reader.ReadBytes((int)length); reader.ReadBytes(4);
            if (type == "tEXt") {
                int zero = Array.IndexOf(data, (byte)0);
                if (zero < 0) continue;
                string key = Encoding.ASCII.GetString(data,0,zero);
                if (key is "chara" or "ccv3") {
                    var parsed = JObject.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(Encoding.ASCII.GetString(data,zero+1,data.Length-zero-1))));
                    if (key == "ccv3") return parsed;
                    card = parsed;
                }
            }
            if (type == "IEND") break;
        }
        return card ?? throw new InvalidDataException("PNG 不含 chara/ccv3 角色卡数据");
    }

    /// <summary>
    /// 角色卡里「可以当上下文用」的字段。前 6 个是酒馆标准的提示词字段；
    /// 后 2 个是兜底 —— 不少「特殊卡」把正文整段放在开场白或作者注释里，
    /// 6 个提示词字段全空，但它们本身是有内容的，不该直接判定成「这张卡无法导入」。
    /// Macro 是该字段对应的酒馆宏，用来判断预设里是否已经占了同一个位置。
    /// </summary>
    public static readonly (string Key, string Label, string Macro)[] CardFields =
    {
        ("description", "描述", "{{description}}"),
        ("personality", "性格", "{{personality}}"),
        ("scenario", "场景", "{{scenario}}"),
        ("system_prompt", "系统指令", "{{system}}"),
        ("mes_example", "对话示例", "{{mesExamples}}"),
        ("post_history_instructions", "历史后指令", "{{postHistoryInstructions}}"),
        ("first_mes", "开场白", "{{greeting}}"),
        ("creator_notes", "作者注释", "{{charCreatorNotes}}")
    };

    /// <summary>标准提示词字段的个数（CardFields 的前 6 个）。</summary>
    public const int StandardFieldCount = 6;

    /// <summary>
    /// 角色卡里的「开场白」全集：<c>first_mes</c> 是第一条，<c>alternate_greetings</c>
    /// 是作者另写的若干条备选开场（v2/v3 规范里是字符串数组，个别卡写成单个字符串或对象）。
    ///
    /// <para><b>为什么需要它</b>：以前只读 <c>first_mes</c>，其余开场白在导入时被静默丢掉 ——
    /// 用户看得见卡里有好几条开场，导入后只剩一条，且没有任何提示。
    /// 现在全部读出来，由调用方决定怎么呈现（装配页做成独立模块，酒馆导入写成世界书条目）。</para>
    ///
    /// <para>返回的每项是 <c>(Label, Text)</c>，Label 形如「开场白」「开场白 2」…，
    /// 已经过滤掉空白项并去掉与第一条重复的内容。顺序与卡里一致。</para>
    /// </summary>
    public static List<(string Label, string Text)> Greetings(JToken? cardData)
    {
        var result = new List<(string, string)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(string? text)
        {
            var value = (text ?? "").Trim();
            if (value.Length == 0) return;
            if (!seen.Add(value)) return;
            result.Add((result.Count == 0 ? "开场白" : "开场白 " + (result.Count + 1), value));
        }
        Add(cardData?.Value<string>("first_mes"));
        // alternate_greetings 的三种形状：数组 / 单字符串 / {"0":"…"} 对象。全都要认。
        switch (cardData?["alternate_greetings"])
        {
            case JArray array:
                foreach (var item in array) Add(item?.Type == JTokenType.Object ? item.Value<string>("content") ?? item.Value<string>("mes") : item?.Value<string>());
                break;
            case JObject map:
                foreach (var property in map.Properties()) Add(property.Value?.Value<string>());
                break;
            default:
                Add(cardData?.Value<string>("alternate_greetings"));
                break;
        }
        return result;
    }

    /// <summary>
    /// 第一条之后的额外开场白（<see cref="Greetings"/> 去掉首条）。
    /// 导入装配计划时用它们生成「角色卡 · 开场白 2 / 3 …」模块。
    /// </summary>
    public static List<(string Label, string Text)> AlternateGreetings(JToken? cardData)
        => Greetings(cardData).Skip(1).ToList();

    /// <summary>额外开场白对应的宏名：第 n 条额外开场白（从 1 数）→ <c>greeting{n+1}</c>。</summary>
    public static string GreetingMacro(int greetingNumber)
        => greetingNumber <= 1 ? "{{greeting}}" : "{{greeting" + greetingNumber + "}}";

    public static List<ContextPlanModule> Preset(JObject root)
    {
        if (root["Entries"] is JArray converted) return converted.OfType<JObject>().OrderBy(x => x.Value<int?>("Order") ?? 0).Select(x => new ContextPlanModule {
            Name=x.Value<string>("Name") ?? "预设", Role=ContextCompiler.Role(x.Value<string>("Role")), Content=x.Value<string>("Content") ?? "",
            Enabled=x.Value<bool?>("Enabled") ?? true, Source="preset"
        }).ToList();
        var prompts = (root["prompts"] as JArray ?? new JArray()).OfType<JObject>().ToList();
        var orders = (root["prompt_order"] as JArray)?.OfType<JObject>().ToList();
        var order = (orders?.FirstOrDefault(x => x.Value<int?>("character_id") == 100001) ?? orders?.LastOrDefault())?["order"] as JArray;
        var result = new List<ContextPlanModule>();
        var sequence = order?.OfType<JObject>().ToList() ?? prompts;
        foreach (var slot in sequence) {
            var id = slot.Value<string>("identifier");
            var p = order == null ? slot : prompts.FirstOrDefault(p => p.Value<string>("identifier") == id);
            if (p == null) continue;
            string text = p.Value<string>("content") ?? "";
            if (p.Value<bool?>("marker") == true) {
                text = id switch { "charDescription"=>"{{description}}", "charPersonality"=>"{{personality}}", "scenario"=>"{{scenario}}", "dialogueExamples"=>"{{mesExamples}}", _=>"" };
            }
            if (string.IsNullOrWhiteSpace(text)) continue;
            result.Add(new ContextPlanModule { Name=p.Value<string>("name") ?? id ?? "预设", Content=text, Role=ContextCompiler.Role(p.Value<string>("role")), Enabled=slot.Value<bool?>("enabled") ?? p.Value<bool?>("enabled") ?? true, Source="preset" });
        }
        if (result.Count == 0 && root.Value<string>("system_prompt") is string system)
            result.Add(new ContextPlanModule { Name="系统提示词", Content=system, Source="preset" });
        if (result.Count == 0) throw new InvalidDataException("未识别到预设提示词");
        return result;
    }

    /// <summary>
    /// 世界书条目行。兼容三种形状：
    /// <list type="bullet">
    /// <item><c>{"entries": {"0": {...}}}</c> —— 酒馆世界书 / 角色卡内嵌 character_book（小写、对象）</item>
    /// <item><c>{"Entries": [ ... ]}</c> —— 本插件 WorldBook.json（大写、数组）</item>
    /// <item><c>[ ... ]</c> —— 已经被 <see cref="NormalizeEntries"/> 规范化过的条目数组</item>
    /// </list>
    ///
    /// <para><b>形状守卫：认不出来就返回空集，绝不抛异常。</b></para>
    /// 这里以前直接写 <c>book?["entries"]</c>，在 JArray 上取字符串键会抛
    /// <c>ArgumentException: Accessed JArray values with invalid key value: "entries"</c>。
    /// 2026-09-30「导入装配资源失败」就是这么炸的：调用方把规范化后的 JArray
    /// 又喂回了 <see cref="MergeWorldBook"/>，后者二次规范化 → 这里收到 JArray。
    /// 导入流程不该因为一次误用（或一张畸形卡）炸掉整个操作。
    /// </summary>
    public static IEnumerable<JObject> EntryRows(JToken? book)
    {
        var entries = book switch
        {
            JArray direct => (JToken?)direct,
            JObject obj => obj["entries"] ?? obj["Entries"],
            _ => null
        };
        return entries switch
        {
            JObject map => map.Properties().Select(p => p.Value).OfType<JObject>(),
            JArray list => list.OfType<JObject>(),
            _ => Enumerable.Empty<JObject>()
        };
    }

    /// <summary>卡内世界书的条目标题（给「要不要导入卡内世界书」的确认弹窗做预览用）。</summary>
    public static List<string> EntryTitles(JToken? book, int max = 6)
        => EntryRows(book)
            .Select(e => e.Value<string>("comment") ?? e.Value<string>("name") ?? e.Value<string>("Title") ?? "")
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Take(max).ToList();

    public static List<ContextPlanModule> World(JToken? book)
    {
        return EntryRows(book).OrderBy(e=>e.Value<int?>("insertion_order") ?? e.Value<int?>("order") ?? e.Value<int?>("InsertionOrder") ?? 100).Select(e=>new ContextPlanModule {
            Name=e.Value<string>("comment") ?? e.Value<string>("name") ?? e.Value<string>("Title") ?? "世界书",
            Content=e.Value<string>("content") ?? e.Value<string>("Content") ?? "",
            Role=ContextCompiler.Role(e.Value<string>("Role") ?? (e["extensions"]?["role"]?.ToString() switch { "1"=>"user", "2"=>"assistant", _=>"system" })),
            Enabled=(e.Value<bool?>("enabled") ?? e.Value<bool?>("Enabled") ?? true) && !(e.Value<bool?>("disable") ?? false),
            Constant=e.Value<bool?>("constant") ?? e.Value<bool?>("Constant") ?? false,
            Keywords=(e["keys"] ?? e["key"] ?? e["Keywords"] ?? new JArray()).Values<string>().Where(x=>x!=null).Cast<string>().ToList(),
            SecondaryKeywords=(e["SecondaryKeywords"] ?? e["secondary_keys"] ?? e["keysecondary"] ?? new JArray()).Values<string>().Where(x=>x!=null).Cast<string>().ToList(),
            Selective=e.Value<bool?>("Selective") ?? e.Value<bool?>("selective") ?? false, CaseSensitive=e.Value<bool?>("CaseSensitive") ?? e.Value<bool?>("case_sensitive") ?? false, Source="worldbook"
        }).ToList();
    }

    /// <summary>
    /// 把任意来源的世界书（酒馆世界书 JSON、角色卡内嵌 character_book、本插件 WorldBook.json）
    /// 规范化成本插件的条目形状 <c>{Id, Title, Keywords, Content, Enabled, Constant, InsertionOrder, UpdatedAt}</c>。
    ///
    /// 为什么必须规范化：酒馆用 <c>entries</c>（小写、可以是对象）+ <c>comment/keys/secondary_keys</c>，
    /// 本插件用 <c>Entries</c>（数组）+ <c>Title/Keywords</c>。以前把酒馆原文直接写进 WorldBook.json，
    /// 结果「世界书」面板读的是 <c>jo["Entries"]</c>，读不到小写的 <c>entries</c>，
    /// 于是「导入成功了但面板里一条都没有」。
    /// </summary>
    /// <param name="fallbackId">
    /// 源条目没有 id 时用什么 id。<c>null</c>（默认）= 随机 Guid。
    ///
    /// 为什么需要它：世界书面板「读取全文」是按 id 回查的，而随机 Guid 每次读都不一样 ——
    /// 同一个没有 id 的世界书文件，两次读取会得到两套 id，点了「读取全文」自然对不上号。
    /// 面板传 <c>i =&gt; "wb" + i</c>（按位置生成），只要文件不变 id 就稳定。
    /// </param>
    public static JArray NormalizeEntries(JToken? book, Func<int, string>? fallbackId = null)
    {
        var result = new JArray();
        var index = 0;
        foreach (var e in EntryRows(book))
        {
            var keywords = new JArray();
            foreach (var k in (e["keys"] ?? e["key"] ?? e["Keywords"] ?? new JArray()).Values<string>())
                if (!string.IsNullOrWhiteSpace(k)) keywords.Add(k);
            var sourceId = (e.Value<string>("id") ?? e.Value<string>("Id") ?? "").Trim();
            result.Add(new JObject {
                ["Id"] = sourceId.Length > 0 ? sourceId : (fallbackId?.Invoke(index) ?? Guid.NewGuid().ToString("N")),
                ["Title"] = e.Value<string>("comment") ?? e.Value<string>("name") ?? e.Value<string>("Title") ?? "世界书条目",
                ["Keywords"] = keywords,
                ["Content"] = e.Value<string>("content") ?? e.Value<string>("Content") ?? "",
                ["Enabled"] = (e.Value<bool?>("enabled") ?? e.Value<bool?>("Enabled") ?? true) && !(e.Value<bool?>("disable") ?? false),
                ["Constant"] = e.Value<bool?>("constant") ?? e.Value<bool?>("Constant") ?? false,
                ["InsertionOrder"] = e.Value<int?>("insertion_order") ?? e.Value<int?>("order") ?? e.Value<int?>("InsertionOrder") ?? 100,
                ["UpdatedAt"] = DateTime.Now
            });
            index++;
        }
        return result;
    }

    /// <summary>把一组条目包成 WorldBook.json 的形状。</summary>
    public static JObject WorldBookFile(JToken? book)
        => new() { ["Entries"] = NormalizeEntries(book), ["UpdatedAt"] = DateTime.Now };

    /// <summary>
    /// 把角色卡内嵌的世界书**合并**进角色已有的 WorldBook.json。
    ///
    /// 判重按「标题 + 正文」：同一条目重复导入不会变成两份；用户自己加过的条目一律保留。
    /// 导入是「加上去」，不是「换掉」—— 静默覆盖用户的 WorldBook.json 等于丢数据。
    /// 返回 (新增, 跳过, 合并后的书)。
    ///
    /// <para><b>调用约定（重要）</b>：<paramref name="incomingEntries"/> 允许是任意形状
    /// （原始酒馆世界书 / 卡内 character_book / 已规范化的条目数组），本方法内部会调
    /// <see cref="NormalizeEntries"/>，而它是幂等的。但调用方<b>不要</b>先把数据规范化再传进来 ——
    /// 那是 2026-09-30 崩溃的写法。形参特意叫 <c>incomingEntries</c> 而不是 <c>incoming</c>，
    /// 就是提醒「这里进的是原始条目，规范化是这一层的事」。</para>
    /// </summary>
    public static (int Added, int Skipped, JObject Book) MergeWorldBook(JObject? existing, JToken? incomingEntries)
    {
        var kept = new JArray();
        if (existing?["Entries"] is JArray array)
            foreach (var row in array.OfType<JObject>()) kept.Add(row.DeepClone());
        var seen = new HashSet<string>(kept.OfType<JObject>().Select(EntryKey), StringComparer.Ordinal);
        var added = 0; var skipped = 0;
        foreach (var entry in NormalizeEntries(incomingEntries).OfType<JObject>())
        {
            if (string.IsNullOrWhiteSpace(entry.Value<string>("Content"))) { skipped++; continue; }
            if (!seen.Add(EntryKey(entry))) { skipped++; continue; }
            kept.Add(entry); added++;
        }
        return (added, skipped, new JObject { ["Entries"] = kept, ["UpdatedAt"] = DateTime.Now });
    }

    /// <summary>合并判重用的键：标题 + 正文（都去掉首尾空白）。</summary>
    static string EntryKey(JObject entry)
        => (entry.Value<string>("Title") ?? entry.Value<string>("comment") ?? entry.Value<string>("name") ?? "").Trim()
           + "\u0000"
           + (entry.Value<string>("Content") ?? entry.Value<string>("content") ?? "").Trim();
}
