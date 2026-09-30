using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Marisa.ContextManager;

/// <summary>
/// 出站 `state` 报文的体积预算。
///
/// 为什么需要它：Electron.NET 的 IPC 桥是一条 Socket.IO 连接，Electron 侧服务端的
/// engine.io 默认 `maxHttpBufferSize` 是 1 MB，超限会被 `413 Payload Too Large`
/// 直接断开，而两端都没有重连逻辑 —— 一次超限就是整程序永久卡死。
/// 更麻烦的是 SocketIOClient 用 System.Text.Json 序列化，非 ASCII 字符会被写成
/// \uXXXX（汉字 6 字节），所以「字符数」和「真正写出去的字节数」差了近一倍。
///
/// 所以这里按**真实转义后的字节数**做预算（见 EscapedBytes），超了就按固定顺序降级：
///   1. 丢掉 planJsonByOwner（渲染进程只读当前选中角色那份，而且本来就会发 plan:get 兜底）
///   2. 丢掉 characterPromptByOwner / runtimeSystemByOwner（前端优先直接读 index.json）
///   3. 条目正文逐级缩水 4000 → 2000 → 800 → 400 → 220 → 80 → 0 字符，并打 Truncated 标记
///      （「本来就短」的正文在最后一档之前不截，见 MinKeepChars）
///   4. 最后一档：正文全部丢掉，只留标题与元数据
///   5. 兜底：条目**数量**本身太多时按数量裁剪（见 Apply 的第 4 步）
/// 渲染进程在编辑被标记 Truncated 的条目之前会先 item:load 取回全文，所以不会丢用户数据。
///
/// ⚠️ **正文该截多少，不能按「谁排在前面」决定。** 按顺序分配预算会让排在后面的短正文
/// 只剩几十字节预览（用户看到的是「正文太大（全文 244 字）」）。计划与世界书两条路径
/// 现在都走 <see cref="FairCap"/> 的统一上限（水填平），只有 state 条目还保留固定阶梯。
///
/// ⚠️ 预算不是「越小越安全」。定得太小会让**用户看得见的正文**被截断，那比什么都糟。
/// 预算的正确下限是「全文正文 + 每条字段开销」，实测约 230 KB（见 DefaultBudgetBytes）。
/// </summary>
public static class ContextStateBudget
{
    /// <summary>
    /// 单条 state / bundle 报文的预算（字节，转义后口径）。
    ///
    /// ⚠️ 这个值的含义是「**一条报文最多装多少**」，不是「必须装下全部」。
    /// 2026-09-30 曾经把它当成后者（设 224 KB、指望它容下全量），结果真实数据
    /// 一进来就触发降级，用户看到的是「消息都显示不完整，还不如上一版」。
    /// 真实数据是会长大的：某角色一天就有 114 条实时消息 + 83 个归档，
    /// 光元数据 190 KB、正文 219 KB；用一个月就是几千条。所以**加大预算解决不了问题**，
    /// 正确的做法是按预算装填 + 其余走分页（见 FillToBudget 与 items:page）。
    ///
    /// 448 KB 的依据：engine.io 的断连线是 1 MB，而出站限速保证 600ms 窗口内总量
    /// 不超过 WindowBytes（512 KB），所以即使 engine.io 把窗口内的报文合并成一个请求，
    /// 也不会越线；单条 448 KB 只有断连线的 44%。
    /// </summary>
    public const int DefaultBudgetBytes = 448 * 1024;

    /// <summary>
    /// 单条报文的硬上限（转义后字节）。超过它一律不发 —— Socket.IO 的 maxHttpBufferSize
    /// 默认 1 MB，超限会直接断开且两端都不重连，一次就是整程序永久卡死。
    /// 这是**兜底网**：正常路径（state / character-bundle / items-page）都走
    /// FillToBudget，天然不超过 DefaultBudgetBytes，不该触发它；
    /// 它拦的是没走预算的报文（例如 plan-state）。
    ///
    /// 放在 ContextStateBudget 而不是 ContextIpcBridge：一是预算与硬上限必须共用同一套
    /// 口径和同一个真值来源，二是这样单元测试不需要引用 Electron.NET 就能验证它。
    /// </summary>
    public const int MaxWireBytes = 576 * 1024;

    /// <summary>
    /// 「正文太大」的统一门槛（字符数）：**不超过这个长度的正文一律原样下发，绝不被截成预览。**
    ///
    /// 2026-09-30 现场：用户看到「这条正文太大，报文里只带了预览（全文 244 字）」。
    /// 244 字被叫作「太大」显然是荒谬的 —— 截它只省下 144 字节，却让用户以为自己的设定丢了。
    /// 根因不是门槛定得低，而是旧的缩水算法**按模块在列表里的先后顺序**分配预算：
    /// 排在前面的长模块把预算吃光，排在后面的短模块只能拿到几十字节的预览，甚至被清空。
    ///
    /// 现在改成「水填平」（见 <see cref="FairCap"/>）：算出一个**统一**的截断上限，
    /// 所有正文按同一个上限截，短的天然不受影响；只要预算还够，上限就不会低于这个门槛。
    /// </summary>
    public const int MinKeepChars = 2000;

    /// <summary>
    /// 降级档位（字符数）。从 4000 起，是因为绝大多数聊天消息都在这个长度以内 ——
    /// 档位定得太低（旧版是 400 起）会让「刚超一点预算」直接掉成 400 字预览，
    /// 用户看到的就是内容突然不完整了。宁可多试几档，也要保住正文。
    ///
    /// ⚠️ 现在只有 <see cref="Apply"/>（state 条目）还在用这套固定阶梯。计划与世界书两条路径
    /// 已改成 <see cref="FairCap"/> 的水填平，<see cref="FitText"/> 改成取「能放下的最长前缀」
    /// —— 阶梯是「按顺序硬砍 + 往固定档位上凑」，前者牺牲排在后面的短正文，
    /// 后者会出现「244 字被截成 220 字」这种既没省多少、又让用户以为丢了内容的截法。
    /// </summary>
    static readonly int[] PreviewLadder = { 4000, 2000, 800, 400, 220, 80, 0 };

    /// <summary>
    /// 水填平：找一个**统一**的截断上限，让一组正文在 <paramref name="budgetBytes"/> 内放下，
    /// 且上限尽量大。返回 (长正文的上限, 短正文的保护线)。
    ///
    /// 为什么要统一上限，而不是「按顺序喂」：按顺序分配时，排在前面的长正文会把预算吃光，
    /// 排在后面的短正文只剩几十字节 —— 于是出现「正文太大（全文 244 字）」这种提示。
    /// 统一上限天然公平：长度不超过上限的正文一个字都不动，超出的各自截到同一个长度。
    ///
    /// 分两档（<paramref name="floorChars"/> 是短正文的保护线，传 <see cref="MinKeepChars"/>）：
    /// <list type="number">
    /// <item><b>够用就保护短正文</b>：把 ≤ floorChars 的正文整个保下来，只在长正文之间水填平。
    /// 少了这一档，一个千万字的长正文会把统一上限压到 floorChars 以下，
    /// 于是 2000 字的短正文也被截成预览 —— 正是用户报的那个现象。</item>
    /// <item><b>实在不够就全体水填平</b>：连「短正文全留 + 长正文各留 1 个字」都放不下时，
    /// 短正文也只能让路。硬约束（报文不许超限）优先于观感。</item>
    /// </list>
    /// 返回的保护线是 0 时表示这一档没生效，调用方对**所有**正文按上限截即可。
    /// </summary>
    static (int Cap, int Floor) FairCap(IReadOnlyList<string> texts, long budgetBytes, int floorChars)
    {
        var longest = 0;
        foreach (var text in texts)
            if (text != null && text.Length > longest) longest = text.Length;
        if (longest == 0) return (0, floorChars);
        if (CappedCost(texts, longest) <= budgetBytes) return (longest, floorChars); // 全都放得下：一个字都不截
        if (budgetBytes <= 0) return (0, 0);

        if (floorChars > 0)
        {
            long shortCost = 0;
            var shortCount = 0;
            var longTexts = new List<string>();
            foreach (var text in texts)
            {
                if (string.IsNullOrEmpty(text)) continue;
                if (text.Length <= floorChars) { shortCost += EscapedBytes(text); shortCount++; }
                else longTexts.Add(text);
            }
            // 短正文全留之后，长正文至少还能各留 1 个字符（12 字节含引号），才走这一档。
            if (shortCount > 0 && shortCost + longTexts.Count * 12 <= budgetBytes)
                return (longTexts.Count == 0 ? floorChars : BinaryCap(longTexts, budgetBytes - shortCost), floorChars);
        }
        return (BinaryCap(texts, budgetBytes), 0);
    }

    /// <summary>二分出最大的统一上限，使所有正文截到该长度后的总字节数不超过预算。</summary>
    static int BinaryCap(IReadOnlyList<string> texts, long budgetBytes)
    {
        var longest = 0;
        foreach (var text in texts)
            if (text != null && text.Length > longest) longest = text.Length;
        if (longest == 0 || budgetBytes <= 0) return 0;
        // CappedCost 关于上限单调不减，所以能二分。
        var low = 0;
        var high = longest;                 // 已知 CappedCost(longest) 超预算
        while (low < high)
        {
            var mid = low + (high - low + 1) / 2;
            if (CappedCost(texts, mid) <= budgetBytes) low = mid; else high = mid - 1;
        }
        return low;
    }

    /// <summary>把一组正文统一截到 <paramref name="cap"/> 之后占用的总字节数。</summary>
    static long CappedCost(IReadOnlyList<string> texts, int cap)
    {
        long total = 0;
        foreach (var text in texts)
        {
            if (string.IsNullOrEmpty(text)) continue;
            total += text.Length <= cap ? EscapedBytes(text) : EscapedBytes(text.Substring(0, cap));
        }
        return total;
    }

    /// <summary>
    /// 按统一上限截断：不超过保护线 <paramref name="floor"/> 的正文原样保留，其余截到 <paramref name="cap"/>。
    /// 保护线为 0 时所有正文一视同仁。
    /// </summary>
    static string ApplyCap(string content, int cap, int floor)
        => content.Length <= cap || (floor > 0 && content.Length <= floor) ? content : content.Substring(0, cap);

    /// <summary>
    /// 一段文本被 System.Text.Json 的默认编码器写进 JSON 字符串后真正占用的字节数。
    ///
    /// 这是整套预算的地基，也是最容易算错的地方：
    ///   1. SocketIOClient 的序列化器是 System.Text.Json（resources/bin 下只有
    ///      SocketIO.Serializer.SystemTextJson.dll，SocketIOClient.dll 里没有任何
    ///      Newtonsoft 引用），而它的默认 JavaScriptEncoder 会把**所有非 ASCII 字符**
    ///      写成 \uXXXX —— 一个汉字从 UTF-8 的 3 字节变成 6 字节。
    ///   2. 所以「UTF-8 字节数」会低估真实报文近一倍。按它设的 512 KB 硬上限，
    ///      实际放行的是约 920 KB —— 正好贴着 engine.io 的 1 MB 断连线。
    /// 这里逐字符精确计算，宁可略微高估，也不要让任何一条报文悄悄越过阈值。
    /// </summary>
    public static long EscapedBytes(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 2; // 空字符串也要两个引号
        long total = 2;
        foreach (var ch in text)
        {
            total += ch switch
            {
                '"' or '\\' => 2,
                '\b' or '\f' or '\n' or '\r' or '\t' => 2,
                // JavaScriptEncoder.Default 会额外转义这几个可能破坏 HTML/JS 的 ASCII 字符。
                '<' or '>' or '&' or '\'' or '+' or '`' => 6,
                < ' ' => 6,
                <= '\u007f' => 1,
                // 其余（含代理对的两个 char）一律 \uXXXX。
                _ => 6
            };
        }
        return total;
    }

    /// <summary>
    /// 按优先级把条目装进预算，返回「要发出去的条目」和「被省略的条数」。
    ///
    /// 这是分页的地基：不再假设「所有条目都塞得进一条报文」，而是
    ///   - `ordered` 由调用方按优先级排好（越靠前越该被包含，通常是「当前角色最新在前」）；
    ///   - 逐条累加，正文放得下就带全文，放不下就只带元数据并置 Truncated（前端会提示点击读取）；
    ///   - 连元数据都放不下时停止，剩下的计入 Omitted，由界面提示「还有 N 条未加载」并走 items:page。
    ///
    /// 这样「一条报文最多多大」由预算硬保证，与用户用了多久、攒了多少条无关 ——
    /// 光靠调大预算只是把爆点往后推（一天 114 条，一个月就是 3000+ 条）。
    /// </summary>
    /// <summary>
    /// 把一段正文压进 <paramref name="remainingBytes"/>。
    ///
    /// 返回 (文本, 是否截断)。装得下就原样返回（一个字都不截）；装不下就取
    /// **能放下的最长前缀** —— 而不是往 4000/2000/800/… 这些固定档位上凑。
    /// 往档位上凑的坏处很直观：244 字的正文会被截成 220 字，只省下 144 字节，
    /// 用户看到的却是「这条正文太大（全文 244 字）」。
    ///
    /// 需要「一组正文之间怎么分配预算」时用 <see cref="FairCap"/>，
    /// 它才是「谁的正文都不该因为排在后面而被牺牲」的那个函数。
    /// </summary>
    public static (string Text, bool Truncated) FitText(string? text, long remainingBytes)
    {
        var value = text ?? "";
        if (value.Length == 0) return ("", false);
        if (EscapedBytes(value) <= remainingBytes) return (value, false);
        var cap = BinaryCap(new[] { value }, remainingBytes);
        return cap <= 0 ? ("", true) : (value.Substring(0, cap), true);
    }

    /// <summary>
    /// 把装配计划压进报文预算：模块正文按剩余预算逐级缩水，缩过的打 truncated + contentLength。
    /// 返回被缩水的模块数。
    ///
    /// 为什么需要它：一张 142 条世界书的角色卡，导入后计划有 143 个模块、正文 9.9 万字，
    /// 序列化后必然超过单条 IPC 报文的硬上限。旧逻辑直接拒发（plan-error），
    /// 用户看到的是「提示导入完成，装配页却一片空白」。
    ///
    /// ⚠️ 操作的是 plan 的 **JSON 副本**，不改调用方那份 —— 同一份 plan 可能马上就要落盘，
    /// 落盘必须是全文（预览一旦写进 Plans/*.json，用户的世界书正文就真的没了）。
    /// 写回路径（save / preview / apply）会按 Id 从磁盘计划回填正文（RestoreTruncatedModules），
    /// 所以前端只持有预览也不会丢数据。
    /// </summary>
    public static int ShrinkPlanForWire(JObject? wire, long fullBytes, int budgetBytes)
    {
        if (wire == null || wire["modules"] is not JArray modules) return 0;
        if (budgetBytes <= 0) budgetBytes = DefaultBudgetBytes;
        // truncated / contentLength 是**派生字段**，每次下发都要重新算。
        // 注意：wire 是从 plan 序列化来的，这两个属性本来就存在（值分别是 false / 0），
        // 所以不能靠「字段不存在」来判断有没有记过 —— 必须先归零。
        foreach (var module in modules)
        {
            module["truncated"] = false;
            module["contentLength"] = 0;
        }
        var truncated = 0;
        // 迭代收敛：缩水本身会给每条被截断的模块新增 contentLength / truncated 字段，
        // 那是要额外占报文字节的。一次算完会超出一两 KB，而超出来的这一点正好又踩到
        // 桥的硬上限（MaxWireBytes）—— 等于白修。所以按「实测还超多少」再缩一轮。
        // 统一上限只降不升（见 ShrinkPlanPass），所以不会来回震荡，最多两三轮回收敛。
        var cap = int.MaxValue;
        for (var pass = 0; pass < 8; pass++)
        {
            truncated = ShrinkPlanPass(modules, fullBytes, budgetBytes, ref cap);
            var measured = EscapedBytes(wire.ToString(Formatting.None));
            if (measured <= budgetBytes) return truncated;
            fullBytes = measured;
        }
        return truncated;
    }

    /// <summary>一轮缩水。返回「正文不是全文」的模块数。</summary>
    static int ShrinkPlanPass(JArray modules, long fullBytes, int budgetBytes, ref int cap)
    {
        // 非正文开销 = 总大小 - 各模块正文大小。id/name/role/keywords 这些字段一个都不能少，
        // 否则装配页会渲染不出结构。
        long overhead = fullBytes;
        var texts = new List<string>(modules.Count);
        foreach (var module in modules)
        {
            var content = module.Value<string>("content") ?? "";
            texts.Add(content);
            overhead -= EscapedBytes(content);
        }
        var available = Math.Max(0, budgetBytes - overhead);

        // 统一上限（水填平）。⚠️ 只降不升：上一轮已经截过的正文不能再长回去，
        // 否则「缩水 → 实测又超 → 再缩」会来回震荡、永远收敛不了。
        var (next, floor) = FairCap(texts, available, MinKeepChars);
        if (next < cap) cap = next;

        var truncated = 0;
        foreach (var module in modules)
        {
            var content = module.Value<string>("content") ?? "";
            if (content.Length > 0)
            {
                var shortened = ApplyCap(content, cap, floor);
                if (shortened.Length < content.Length)
                {
                    // contentLength 只在第一次缩水时记录：第二轮 pass 拿到的 content
                    // 已经是预览，再记一次就把「全文 N 字」写成预览长度了。
                    var full = module.Value<int?>("contentLength") is > 0 ? module.Value<int>("contentLength") : content.Length;
                    module["content"] = shortened;
                    module["contentLength"] = full;
                    module["truncated"] = true;
                }
            }
            // 按标记统计，而不是按「这一轮有没有进 if」——
            // 上一轮缩过的模块这一轮可能已经装得下，但它的正文仍然是预览。
            if (module.Value<bool?>("truncated") == true) truncated++;
        }
        return truncated;
    }

    /// <summary>
    /// 世界书条目行按预算打包成「要下发的行」。返回 (行, NextOffset, 被截断条数)。
    ///
    /// <para><paramref name="fullText"/> = false（首次打开面板）：**每条都要出现**。
    /// 正文放得下给全文，放不下给预览并打 truncated —— 一张 40 条长条目的卡，
    /// 世界书正文就有 7.8 万字，序列化后 800+ KB，超过单条 IPC 报文的硬上限，
    /// 旧逻辑会被桥直接拦掉，用户看到的是「面板一条都没有」。现在至少结构完整可见。</para>
    ///
    /// <para><paramref name="fullText"/> = true（worldbook:page，按需读全文）：
    /// 装到预算满就停，回传 NextOffset 让前端翻页；每页都是**完整正文**。</para>
    /// </summary>
    public static (List<JObject> Rows, int NextOffset, int TruncatedCount) PackWorldBookRows(
        IReadOnlyList<JObject>? rows, int offset, bool fullText, int budgetBytes)
    {
        var list = new List<JObject>();
        var start = Math.Max(0, offset);
        if (rows == null || rows.Count == 0) return (list, start, 0);
        if (budgetBytes <= 0) budgetBytes = DefaultBudgetBytes;

        // 第一阶段：先只用**元数据**定下这一页装得下哪些条目（不碰正文，所以很便宜）。
        // 连元数据都放不下的停在这里，剩下的交给「还有 N 条未加载」提示 + 翻页。
        var page = new List<(JObject Meta, string Content)>();
        long metaUsed = 0;
        var index = start;
        for (; index < rows.Count; index++)
        {
            var row = rows[index];
            var meta = WorldBookMeta(row);
            var metaCost = EscapedBytes(meta.ToString(Formatting.None));
            if (metaUsed + metaCost > budgetBytes) break;
            metaUsed += metaCost;
            page.Add((meta, row.Value<string>("Content") ?? ""));
        }
        if (page.Count == 0) return (list, index, 0);

        // 第二阶段：正文按剩余预算做**统一上限**（水填平）。
        // 以前是「按顺序喂」：排在前面的长条目把预算吃光，后面的短条目只剩几十字节预览
        // —— 于是出现「正文太大（全文 244 字）」这种荒谬提示。
        // 每条正文除元数据外还要带 content / contentLength / truncated 三个字段（约 60 字节），
        // 先把这部分留出来，免得算出来的上限让整页正好超出预算。
        const int PerRowFieldReserve = 64;
        var contentBudget = budgetBytes - metaUsed - (long)PerRowFieldReserve * page.Count;
        var (cap, floor) = fullText
            ? (int.MaxValue, 0)
            : FairCap(page.Select(p => p.Content).ToList(), contentBudget, MinKeepChars);

        long used = 0;
        var truncatedCount = 0;
        foreach (var entry in page)
        {
            var content = entry.Content;
            string text;
            bool cut;
            if (fullText)
            {
                // 翻页模式：每条都给完整正文，放不下就整条留给下一页（保证每页都是完整正文）。
                var fullCost = EscapedBytes(content);
                // **第一页至少要出一条**，否则 NextOffset 不前进，前端会死循环。
                if (used + fullCost > budgetBytes && list.Count > 0) break;
                if (used + fullCost <= budgetBytes) { (text, cut) = (content, false); }
                else { (text, cut) = FitText(content, Math.Max(0, budgetBytes - used)); }
            }
            else
            {
                text = ApplyCap(content, cap, floor);
                cut = text.Length < content.Length;
            }
            if (cut) truncatedCount++;

            var payload = new JObject
            {
                ["id"] = entry.Meta.Value<string>("id") ?? "",
                ["title"] = entry.Meta.Value<string>("title") ?? "",
                ["keywords"] = entry.Meta["keywords"]?.DeepClone() ?? new JArray(),
                ["content"] = text,
                ["enabled"] = entry.Meta.Value<bool?>("enabled") ?? true,
                ["constant"] = entry.Meta.Value<bool?>("constant") ?? false,
                ["insertionOrder"] = entry.Meta.Value<int?>("insertionOrder") ?? 100,
                ["contentLength"] = content.Length,
                ["truncated"] = cut
            };
            used += EscapedBytes(payload.ToString(Formatting.None));
            list.Add(payload);
        }
        // ⚠️ NextOffset 必须按**实际发出去的条数**算，不能用上面那个元数据游标：
        // 正文循环会因为放不下而提前 break，那时 page 里还有条目没发出去 ——
        // 用元数据游标会让它们被整段跳过（翻页漏条，而且完全静默）。
        return (list, start + list.Count, truncatedCount);
    }

    /// <summary>世界书条目的元数据（不含正文）。第一阶段的「装得下几条」只用它来算。</summary>
    static JObject WorldBookMeta(JObject row) => new()
    {
        ["id"] = row.Value<string>("Id") ?? "",
        ["title"] = row.Value<string>("Title") ?? "",
        ["keywords"] = new JArray((row["Keywords"] as JArray)?.Select(x => x?.ToString() ?? "")
            .Where(x => x.Length > 0).ToList() ?? new List<string>()),
        ["enabled"] = row.Value<bool?>("Enabled") ?? true,
        ["constant"] = row.Value<bool?>("Constant") ?? false,
        ["insertionOrder"] = row.Value<int?>("InsertionOrder") ?? 100
    };

    public static (List<ContextItem> Items, int Omitted) FillToBudget(
        IReadOnlyList<ContextItem> ordered, int budgetBytes, int reserveBytes)
    {
        var kept = new List<ContextItem>();
        if (ordered == null || ordered.Count == 0) return (kept, 0);
        if (budgetBytes <= 0) budgetBytes = DefaultBudgetBytes;

        long used = Math.Max(0, reserveBytes);
        var omitted = 0;
        for (var index = 0; index < ordered.Count; index++)
        {
            var item = ordered[index];
            var full = ItemCost(item);
            // 先试带全文。
            if (used + full <= budgetBytes)
            {
                used += full;
                kept.Add(item);
                continue;
            }
            // 放不下全文：只带元数据，前端会把它标成「未加载、点击读取全文」。
            var meta = ItemCost(CloneWithoutContent(item));
            if (used + meta > budgetBytes)
            {
                // 连元数据都放不下了：从这里开始全部省略。
                omitted = ordered.Count - index;
                break;
            }
            var stripped = CloneWithoutContent(item);
            stripped.Truncated = true;
            used += meta;
            kept.Add(stripped);
        }
        return (kept, omitted);
    }

    /// <summary>
    /// 装填优先级分档。0 = 配置/提示词类小条目，必须优先进入报文；1 = 普通条目。
    ///
    /// 为什么需要这一档：只按 UpdatedAt 倒序排，会出现「用户三个月没改角色设定，
    /// 于是角色提示词被最新几条聊天挤出去」——而这类条目恰恰是**小但关键**的：
    /// 提示词工作室的文本框、角色管理页的摘要、装配页的「角色卡独立系统提示词」全靠它，
    /// 被挤出去界面就是空白。消息类条目被挤出去只是需要分页，代价完全不同。
    /// </summary>
    public static int FillTier(ContextItem item) => item.SourceKey switch
    {
        "character-prompt" => 0,
        "offline-system" => 0,
        "live-system" => 0,
        "global-config" => 0,
        _ => 1
    };

    /// <summary>
    /// 统一的装填顺序：当前查看角色优先 → 配置类优先 → 更新时间倒序。
    ///
    /// ⚠️ state 装填、character-bundle、items:page **三处必须用同一个函数**。
    /// 前端把「我已经持有该角色的多少条」当作 offset 传回 items:page，后端 Skip(offset)；
    /// 只要两边的排序口径有一点不同，翻页就会漏条或重复（用户会看到某些消息永远不出现）。
    /// 注意「同一个角色内部的相对顺序」只由 FillTier + UpdatedAt 决定，与 focused 无关，
    /// 所以中途切换当前角色也不会让已经翻过的页错位。
    /// </summary>
    public static List<ContextItem> OrderForFill(IEnumerable<ContextItem> items, string? focused)
        => items
            .OrderBy(i => string.Equals(i.Owner, focused, StringComparison.Ordinal) ? 0 : 1)
            .ThenBy(FillTier)
            .ThenByDescending(i => i.UpdatedAt ?? DateTime.MinValue)
            .ToList();

    /// <summary>单条条目在网线上的字节数（含 JSON 结构开销的近似）。</summary>
    static long ItemCost(ContextItem item)
    {
        try
        {
            var json = JsonConvert.SerializeObject(item, ContextJsonSettings.Wire);
            // 加 1 字节给数组里的逗号/括号分摊。
            return EscapedBytes(json) + 1;
        }
        catch
        {
            return 512;
        }
    }

    /// <summary>复制一份条目并清掉正文（含附件里的文本片段，见 TruncateItems 的说明）。</summary>
    static ContextItem CloneWithoutContent(ContextItem item)
    {
        var clone = new ContextItem
        {
            Id = item.Id, Category = item.Category, Kind = item.Kind, Modality = item.Modality,
            Owner = item.Owner, Title = item.Title, Content = "", Path = item.Path,
            Source = item.Source, SourceKey = item.SourceKey, Origin = item.Origin,
            Size = item.Size, EstimatedTokens = item.EstimatedTokens,
            MemoryLevel = item.MemoryLevel, Enabled = item.Enabled, ReadOnly = item.ReadOnly,
            Truncated = item.Truncated, CreatedAt = item.CreatedAt, UpdatedAt = item.UpdatedAt,
            Tags = item.Tags, Attachments = new List<ContextAttachment>()
        };
        foreach (var attachment in item.Attachments ?? new List<ContextAttachment>())
        {
            clone.Attachments.Add(new ContextAttachment
            {
                Modality = attachment.Modality, Status = attachment.Status, Title = attachment.Title,
                Path = attachment.Path, Detail = "", Size = attachment.Size
            });
        }
        return clone;
    }

    /// <summary>
    /// 按网线格式（紧凑 JSON、camelCase）测量快照真正写出去的字节数。
    /// 注意是**转义后**的字节数，不是 UTF-8 字节数：见 EscapedBytes 的说明。
    /// </summary>
    public static int Measure(ContextSnapshot? snapshot)
    {
        if (snapshot == null) return 0;
        try
        {
            var json = JsonConvert.SerializeObject(snapshot, ContextJsonSettings.Wire);
            var escaped = EscapedBytes(json);
            return escaped > int.MaxValue ? int.MaxValue : (int)escaped;
        }
        catch
        {
            // 测不出来就不要因为预算逻辑把整条状态推送搞崩；交给桥的硬上限兜底。
            return 0;
        }
    }

    /// <summary>
    /// 就地降级直到报文进入预算，并记录降级步骤。返回同一个实例，方便链式使用。
    /// </summary>
    public static ContextSnapshot Apply(ContextSnapshot snapshot, int budgetBytes)
    {
        if (snapshot == null) return snapshot!;
        if (budgetBytes <= 0) budgetBytes = DefaultBudgetBytes;

        var steps = new List<string>();
        if (Measure(snapshot) > budgetBytes)
        {
            // 1. 计划 JSON 是「字符串里的 JSON」，会被外层再转义一次，通常是最贵的一块。
            //    但它也可能只有几百字节 —— 那就没必要为省几百字节牺牲「state 里直接带计划」。
            //    只有它确实够大（超过预算的四分之一）才丢。
            if (snapshot.PlanJsonByOwner.Count > 0 &&
                WireCost(snapshot.PlanJsonByOwner.Values) > budgetBytes / 4)
            {
                snapshot.PlanJsonByOwner.Clear();
                steps.Add("drop-plans");
            }

            // 2. 角色提示词 / 运行时系统消息同理（前端优先直接读 index.json）。
            if (Measure(snapshot) > budgetBytes &&
                WireCost(snapshot.CharacterPromptByOwner.Values.Concat(snapshot.RuntimeSystemByOwner.Values)) > budgetBytes / 4)
            {
                snapshot.CharacterPromptByOwner.Clear();
                snapshot.RuntimeSystemByOwner.Clear();
                steps.Add("drop-prompts");
            }

            // 3. 条目正文逐级缩水。只试「比当前最长正文还短」的档位，
            //    免得在日志里留下一堆没实际作用的步骤。
            foreach (var cap in PreviewLadder)
            {
                if (Measure(snapshot) <= budgetBytes) break;
                if (LongestItemContent(snapshot) <= cap) continue;
                TruncateItems(snapshot, cap);
                steps.Add(cap == 0 ? "items-content-dropped" : $"items-preview-{cap}");
            }

            // 4. 兜底：条目**数量**本身就能把报文顶爆 —— 每条光 JSON 字段名就要约 450 字节，
            //    正文清空后 400 条仍有 180 KB。这一步按「最近更新」保留放得下的那些，
            //    其余不列出并在界面上说明。有了它，「任何快照都能落进预算」这个不变量
            //    才对任意条目数都成立 —— 这是「插件永远不会发出超限报文」的最后一道保证。
            if (Measure(snapshot) > budgetBytes && snapshot.Items.Count > 1)
            {
                var total = Measure(snapshot);
                var perItem = Math.Max(1, total / snapshot.Items.Count);
                // 留 12% 余量给摘要与诊断字段；perItem 里本来就含这部分开销，所以偏保守。
                var keep = (int)Math.Max(1, (long)budgetBytes * 88 / 100 / perItem);
                if (keep < snapshot.Items.Count)
                {
                    var kept = snapshot.Items
                        .OrderByDescending(i => i.UpdatedAt ?? DateTime.MinValue)
                        .Take(keep)
                        .OrderBy(i => i.Owner, StringComparer.Ordinal)
                        .ThenBy(i => i.Title, StringComparer.Ordinal)
                        .ToList();
                    snapshot.HiddenItems = snapshot.Items.Count - kept.Count;
                    snapshot.Items = kept;
                    steps.Add($"items-trimmed-{kept.Count}");
                }
            }
        }

        snapshot.Degraded = steps.Count > 0;
        snapshot.DegradeSteps = steps;
        return snapshot;
    }

    /// <summary>
    /// 估算一组字符串真正占用网线的字节数（同样是转义后的口径，和 Measure 一致）。
    /// 只用于「值不值得丢」的判断，最终是否达标仍由 Measure 的真实字节数决定。
    /// </summary>
    static long WireCost(IEnumerable<string> values)
    {
        long total = 0;
        foreach (var value in values)
        {
            if (string.IsNullOrEmpty(value)) continue;
            total += EscapedBytes(value);
        }
        return total;
    }

    static int LongestItemContent(ContextSnapshot snapshot)
    {
        var longest = 0;
        foreach (var item in snapshot.Items)
        {
            var length = item.Content?.Length ?? 0;
            if (length > longest) longest = length;
            if (item.Attachments == null) continue;
            foreach (var attachment in item.Attachments)
            {
                var detail = attachment.Detail?.Length ?? 0;
                if (detail > longest) longest = detail;
            }
        }
        return longest;
    }

    /// <summary>
    /// 把超过 cap 个字符的正文截断并打标记；返回被改动的条目数。
    ///
    /// ⚠️ 必须**同时**处理 Attachments[].Detail。历史上这里只清 item.Content，
    /// 而附件里那份「文本片段」存的是全文副本，于是 items-content-dropped 之后
    /// 正文照样在报文里 —— 降级看起来生效了，实际一个字都没少（2026-09-30 现场：
    /// 声称「正文全部改为按需读取」之后报文仍有 317 KB）。任何新增的
    /// 「可能存放正文的字段」都必须在这里一起清掉，否则阶梯就是假的。
    /// </summary>
    static int TruncateItems(ContextSnapshot snapshot, int cap)
    {
        var changed = 0;
        foreach (var item in snapshot.Items)
        {
            // 「本来就短」的正文不截：截它只省几十几百字节，用户却看到「正文太大（全文 244 字）」。
            // 判据用 `!item.Truncated`（**当前长度就是原文长度**）而不是「长度 ≤ 门槛」——
            // 已经截过的条目现在的长度是上一档的结果，不是原文长度，必须让它继续跟着阶梯往下走，
            // 否则阶梯会在 2000 字那一档卡住、再也压不进预算。
            // cap == 0 是最后一档（正文全部改为按需读取），此时不设保护。
            var wasTruncated = item.Truncated;
            var content = item.Content ?? "";
            if (content.Length > cap && !(cap > 0 && !wasTruncated && content.Length <= MinKeepChars))
            {
                item.Content = cap == 0 ? "" : content[..cap];
                item.Truncated = true;
                changed++;
            }
            if (item.Attachments == null) continue;
            foreach (var attachment in item.Attachments)
            {
                var detail = attachment.Detail ?? "";
                if (detail.Length <= cap) continue;
                if (cap > 0 && !wasTruncated && detail.Length <= MinKeepChars) continue;
                attachment.Detail = cap == 0 ? "" : detail[..cap];
                changed++;
            }
        }
        return changed;
    }

    /// <summary>给界面用的一句话说明。</summary>
    public static string Describe(IReadOnlyList<string> steps)
    {
        if (steps == null || steps.Count == 0) return "";
        var parts = new List<string>();
        foreach (var step in steps)
        {
            parts.Add(step switch
            {
                "drop-plans" => "装配计划改为按需读取（plan:get）",
                "drop-prompts" => "角色提示词改为按需读取（native-prompt:get / index.json）",
                "items-content-dropped" => "条目正文全部改为按需读取",
                _ when step.StartsWith("items-preview-", StringComparison.Ordinal) =>
                    "条目正文预览截断到 " + step["items-preview-".Length..] + " 字",
                _ when step.StartsWith("items-trimmed-", StringComparison.Ordinal) =>
                    "条目过多，只列出最近 " + step["items-trimmed-".Length..] + " 条",
                _ => step
            });
        }
        return string.Join("；", parts);
    }
}
