#pragma warning disable SKEXP0001
#pragma warning disable SKEXP0110
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Alife.Foundation;
using Alife.Framework;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using ElectronNET.API;
using ElectronNET.API.Entities;
using Microsoft.SemanticKernel.ChatCompletion;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using Newtonsoft.Json.Linq;

namespace Marisa.ContextManager;

public sealed partial class ContextManagerRuntime
{
    const string MemoryModuleId = "Alife.Function.Memory.MemoryService";
    // 与 Resources/ContextManager/app.js 里的 APP_BUILD 保持一致。
    // 后端把它放进 state.diagnostics，渲染进程据此发现“加载的是旧 app.js”。
    // f：分页装填定稿（统一 OrderForFill、按角色的 OmittedByOwner、补回 character-prompt）。
    // g：修「小回包被出站合并吃掉导致 15 秒超时」（Coalescible 只留 state）+ 去掉每次
    //    state 都重拉 character-bundle 的风暴。
    // h：① 角色快照带上设定来源（酒馆卡 / 世界书 / 酒馆预设原文）；
    //    ② 角色卡导入不再删除 native 模块（修「角色设定」分区忽隐忽现）；
    //    ③ 导入栏加「带卡内世界书」；④ 提示词字段全空的「特殊卡」按基本信息导入；
    //    ⑤ 装配页头部改为讲解「插件覆盖 vs 本地覆盖」。
    // i：① 修「导入装配资源失败」崩溃（NormalizeEntries 被调两次，EntryRows 收到 JArray）；
    //    ② 卡内世界书改为「检测到就弹窗询问」（装配页与酒馆兼容页共用同一套策略 + 记忆）；
    //    ③ 世界书面板保存不再丢掉 comment/keys/position 等酒馆字段，且保存前备份。
    // j：修「导入后显示导入完成，但装配页什么也没有」+「worldbook-state 864 KB 被拦」：
    //    ① plan-state / worldbook-state 以前不走预算装填，142 条世界书的计划（正文 9.9 万字）
    //       和 40 条长条目的世界书（正文 7.8 万字）序列化后都超过单条 IPC 报文硬上限；
    //       现在**结构全给、正文按预算给预览**并打 truncated，用户点「读取全文」按需取回；
    //    ② 写回路径（save / preview / apply）一律按 Id 从磁盘计划回填正文（RestoreTruncatedModules），
    //       所以前端只持有预览也不会丢数据；
    //    ③ 导入回包顺序固定为「先 plan-state，再 plan-imported」——
    //       以前 SendPlan 拒发时先发 plan-error，紧接着的「导入完成」把错误弹窗顶掉。
    // k：① 「卡内世界书」确认不再用 Electron 原生 MessageBox（Windows 系统对话框：灰底、系统字体、
    //       标题栏写的是应用名，和插件界面完全不是一套），改成「后端问、前端答」——
    //       后端发 card-worldbook-ask，前端用插件自己的 openModal 渲染，
    //       答案走 card-worldbook-answer 回来（AskUiAsync / AnswerUiAsk）；
    //    ② 弹窗等用户决定期间，前端把待回请求的看门狗统一推后，不会误报「后端无响应」。
    // l：① 右栏加「模块搜索」：143 个模块的计划不用再靠肉眼滚，搜名称/正文/关键词直接定位；
    //    ② 角色卡的多条开场白（alternate_greetings）不再只取第一条，全部读出来分别成模块
    //       （角色卡 · 开场白 2 / 3 …），并给出 {{greeting2}}、{{greeting3}} 宏；
    //    ③ 宏设置加「自动宏应用」（ContextPlan.AutoMacros）：本插件没有酒馆那么丰富的宏能力，
    //       未实现也没定义的宏（{{lastPrompt}} 等）直接填成宏名本身，{{char}} 仍取角色名；
    //    ④ 编辑大窗口的内容区块旁加「⤢ 放大」：把内容编辑器顶到整个弹窗，
    //       隐藏模块名称 / 输出身份 / 区域 / 区域内顺序这些前置字段。
    // m：① 「正文太大」的门槛统一到 2000 字（ContextStateBudget.MinKeepChars）：
    //       不超过 2000 字的正文一律原样下发，绝不再被截成「预览」（用户现场那条 244 字）。
    //    ② 瘦身算法从「按列表顺序喂预算」改成**水填平**（FairCap / BinaryCap）：
    //       预算在长正文之间平均分，短正文受保护 —— 排在长模块后面的短正文不再被牺牲。
    //    ③ 世界书分页同样两阶段：先用元数据定「这一页装得下谁」，再按剩余预算统一截。
    // n：① 自动后台拉全文（前端 `autoFetchPlanModules` / `pumpAutoPlanFetch`）：
    //       打开装配页或世界书面板后，只有预览的模块/条目会在**后台串行**读回全文，
    //       不用再手点「读取全文」。串行是硬约束 —— 143 个请求一起发会把回包挤成
    //       出站风暴（限速 600ms / 512 KB）；另有 15 秒单条超时保护，一条丢包不会卡死队列。
    //    ② 控制栏加「自动读取全文」开关（localStorage 持久化，默认开）。关掉即回到
    //       「点了才读」，并在换角色 / 关开关时清空队列（resetAutoFetch）。
    //    ③ 门槛（MinKeepChars）保持 2000 不动 —— 提高它会让「短正文保护档」更容易失效，
    //       反而截到更短的正文；决定能不能全显示的是总字节数（448 KB ≈ 7.6 万字）。
    //       这套换算已固化成第 15 轮断言。
    // o：① 标题栏「最小化」图标画不出来：`.ic-min:after` 是**行内级**伪元素，
    //       width/height 对它不生效，那条 1px 横线根本没渲染（`.ic-max` 的边框画在元素
    //       本身上、`.ic-close` 是绝对定位，所以只有最小化坏了）。功能一直在，只是看不见。
    //    ② 弹窗层加 `-webkit-app-region: no-drag`：Electron 的拖拽区是**窗口级**命中测试，
    //       标题栏那条会吃掉压在它上面的控件的点击（与 z-index 无关）。
    //    ③ 保存方案的命名弹窗：实时说明「这个名字会存到哪里」—— 名称等于角色名时后端会写进
    //       自动快照槽而不是新建命名快照，以前只在说明里带一句，用户填完发现列表没多出东西。
    //       同时打开即聚焦 + 全选预填的名字，不用手动拖选。
    //    ④ 清掉 ContextManagerConfig 里 6 个**全项目没人读**的字段（含 `MaxPreviewChars`），
    //       框架配置面板上不再出现改了没反应的开关。
    // p：长时间运行审计（第 16 轮）。三处「会随使用时长增长」的东西收口：
    //    ① `renderer-trace.log` 以前**完全没有上限**，而且是跨会话追加的（实测已 561 KB /
    //       8841 行，里面还留着更早构建号的 boot 行）。现在前端自己按 512 KB 封顶，
    //       超了就只留后半段重写 —— 与后端 ContextTrace 的 600 KB 上限同一套做法。
    //       顺带把「每条日志一次 statSync」省掉：起始大小只量一次，之后自己累加字节数。
    //    ② `Release()`（插件卸载）以前不摘 `LanguageModel.ContextTransform`。那个委托是闭包，
    //       捕获了 activity 与运行时实例。插件重载而模型存活时：新运行时因为属性非空而挂不上
    //       （插件覆盖静默失效，trace 里只有 attached=false），旧运行时还被闭包引用着回收不掉。
    //       `DetachTransforms()` 早就写好了却没人调用，现在在 Release 里补上。
    //    ③ 删掉 `int references;` —— 全项目只声明、从未读写。
    // 其余部分审计结论是**不会累积**：出站队列有界（256 条，满则丢新）、
    // 窗口/定时器在关窗与卸载时逐个释放、state 每次整份替换旧对象可回收、
    // 且界面全程是 DOM/CSS，没有 WebGL/canvas/video —— 显存占用恒定（只有一个窗口的合成开销）。
    public const string AppBuild = "cm-app-2026-09-30-p";

    // state 报文的体积预算（字节）。见 ContextStateBudget 的注释：
    // 报文过大（中文被序列化成 \uXXXX 后接近 1 MB）会触发 Socket.IO 的 413 断连，
    // 而桥没有重连逻辑 —— 那一次卡死只能重启 Alife。
    const int StateBudgetBytes = ContextStateBudget.DefaultBudgetBytes;

    // 单个角色的装配计划内联进 state 的上限。计划里存的是各模块全文，某角色那份
    // 33 个模块就有 187 KB（转义后 320 KB），内联进去等于把 state 直接顶爆。
    // 超过这个上限就不内联，交给 plan:get / plan-state 单条下发。
    // 注意内联的计划会占用 FillToBudget 的「保留量」，所以这个值不能大：
    // 它挤掉的是用户正在看的条目正文。
    const int PlanJsonInlineLimit = 48 * 1024;

    static ContextManagerRuntime? current;
    readonly CharacterSystem characterSystem;
    readonly ChatActivitySystem chatActivitySystem;
    readonly StorageSystem storageSystem;
    readonly PluginSystem pluginSystem;
    readonly ModuleSystem moduleSystem;
    readonly ILogger logger;
    ContextWindowService? windowService;
    readonly object stateLock = new();
    ContextOverrideBuilder? overrideBuilder;
    bool activationHooked;
    ContextPlanService? planService;
    readonly object historySubscriptionLock = new();
    readonly Dictionary<object, Action> historySubscriptions = new();
    System.Threading.Timer? historyRefreshTimer;
    // 自动侧装的补试定时器：个别加载顺序下 LanguageModel 还没就绪，补几次即可，之后自行停止。
    System.Threading.Timer? autoAttachTimer;
    int autoAttachRetries;
    // 渲染进程「正在查看」的角色：由任何带 owner 的 IPC 更新。
    // state 里只有它需要带计划 JSON 和提示词全文 —— 前端也只读这几个字段的当前角色那份。
    volatile string focusedOwner = "";

    public ContextManagerConfig Config { get; private set; } = new();

    ContextManagerRuntime(CharacterSystem characterSystem, ChatActivitySystem chatActivitySystem,
        StorageSystem storageSystem, PluginSystem pluginSystem, ModuleSystem moduleSystem, ILogger logger)
    {
        this.characterSystem = characterSystem;
        this.chatActivitySystem = chatActivitySystem;
        this.storageSystem = storageSystem;
        this.pluginSystem = pluginSystem;
        this.moduleSystem = moduleSystem;
        this.logger = logger;
        ContextTrace.Configure(FindStorageRoot());
        // 记录实际被加载的程序集文件与写入时间：用来判断 Alife 跑的是不是这次编译的产物。
        var location = GetType().Assembly.Location;
        var built = string.IsNullOrWhiteSpace(location) || !File.Exists(location)
            ? "(in-memory)"
            : new FileInfo(location).LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss");
        ContextTrace.Write($"runtime created build={AppBuild} assembly={location} built={built}");
        chatActivitySystem.Activated += OnActivityActivated;
        chatActivitySystem.Deactivated += OnFrameworkActivityDeactivated;
        // 插件一加载就对**已经激活**的角色做一次自动侧装（Alife 先创建活动再加载插件的顺序下，
        // Activated 事件不会补发，所以这里必须主动扫一遍）。
        TryAutoAttachAll();
        // 个别加载顺序下 LanguageModel / ChatBot 还没就绪，补试几次（2.5s 间隔，最多 3 次）。
        try
        {
            autoAttachTimer = new System.Threading.Timer(_ =>
            {
                TryAutoAttachAll();
                if (System.Threading.Interlocked.Increment(ref autoAttachRetries) >= 3)
                {
                    autoAttachTimer?.Dispose();
                    autoAttachTimer = null;
                }
            }, null, 2500, 2500);
        }
        catch (Exception ex) { ContextTrace.Write("auto attach timer failed: " + ex.Message); }
    }

    void OnActivityActivated(ChatActivity activity)
    {
        try
        {
            SubscribeHistory(activity);
            var owner = activity.Character.Name;
            // 自动侧装：角色一激活就按它保存的配置生效（插件覆盖 → 挂上请求前重组）。
            // 这是“第二次只启用上下文管理器插件就能直接用”的关键，用户不必再手动点
            // “应用到当前角色”。
            TryAutoAttach(activity);
            // 临时覆盖（旧版纯文本注入）：激活完成后，把插件保存的内容重新注入 index[0]
            var temp = ReadTempOverride(owner);
            if (!string.IsNullOrWhiteSpace(temp))
            {
                activity.ChatBot.EditChatHistory(thread =>
                {
                    if (thread.ChatHistory.Count > 0)
                        thread.ChatHistory[0].Content = temp;
                }, "ContextManager 临时覆盖注入");
            }
        }
        catch (Exception ex) { logger.LogWarning(ex, "激活后注入临时覆盖失败"); }
    }

    void SubscribeHistory(ChatActivity activity)
    {
        lock (historySubscriptionLock)
        {
            if (historySubscriptions.ContainsKey(activity.ChatBot)) return;
            Action callback = () => ScheduleHistoryRefresh();
            historySubscriptions[activity.ChatBot] = callback;
            activity.ChatBot.ChatHistoryEdited += callback;
        }
    }

    void OnFrameworkActivityDeactivated(ChatActivity activity)
    {
        lock (historySubscriptionLock)
        {
            if (historySubscriptions.Remove(activity.ChatBot, out var callback))
                activity.ChatBot.ChatHistoryEdited -= callback;
        }
        try { OnActivityDeactivated(activity); }
        catch (Exception ex) { logger.LogDebug(ex, "卸载角色的上下文转换器失败"); }
        ScheduleHistoryRefresh();
    }

    void ScheduleHistoryRefresh()
    {
        lock (historySubscriptionLock)
        {
            historyRefreshTimer ??= new System.Threading.Timer(_ =>
            {
                if (windowService?.Window != null) QueueInitialState();
            }, null, System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
            historyRefreshTimer.Change(350, System.Threading.Timeout.Infinite);
        }
    }

    public static ContextManagerRuntime GetOrCreate(CharacterSystem c, ChatActivitySystem ca, StorageSystem s,
        PluginSystem p, ModuleSystem m, ILogger l)
        => current ??= new ContextManagerRuntime(c, ca, s, p, m, l);

    // 落盘用缩进版：方便用户直接打开 CharacterPresets / Plans 里的文件看。
    static readonly JsonSerializerSettings SnapshotJsonSettings = ContextJsonSettings.Disk;

    // 上线用紧凑版：state 报文里的 planJsonByOwner 是「字符串里的 JSON」，
    // 会被外层再转义一次，缩进版会白白放大 30%+ 的报文体积。
    static readonly JsonSerializerSettings WirePlanJsonSettings = ContextJsonSettings.Wire;

    public async Task OpenWindowAsync()
    {
        var wwwRoot = Path.Combine(
            pluginSystem.PluginContext.GetPluginDirectoryPath("Marisa.ContextManager"),
            "Resources", "ContextManager");
        ContextTrace.Configure(FindStorageRoot());
        ContextTrace.Write("open window wwwRoot=" + wwwRoot);

        windowService ??= new ContextWindowService(logger);
        windowService.OnMessage -= OnRendererMessage;
        windowService.OnMessage += OnRendererMessage;
        windowService.RendererReady -= OnRendererReady;
        windowService.RendererReady += OnRendererReady;
        // 不传 snapshot：窗口立即打开，加载完成后只推送一次实时数据
        await windowService.OpenAsync(wwwRoot, "");
    }

    void OnRendererReady()
    {
        try
        {
            // 打开窗口时再兜一次：角色可能是在插件加载之后才激活的。
            TryAutoAttachAll();
            QueueInitialState();
        }
        catch (Exception ex) { logger.LogError(ex, "推送 ContextManager 初始状态失败"); }
    }

    void QueueInitialState()
    {
        _ = Task.Run(() =>
        {
            try { PushInitialState(); }
            catch (Exception ex)
            {
                logger.LogError(ex, "后台构建 ContextManager 初始状态失败");
                try { SendWindow("error", new { message = "初始化失败：" + ex.Message }); } catch { }
            }
        });
    }

    // 快照必须在锁内构建，但**绝不能**在锁内发送：
    // Electron.NET 的 IpcMain.Send 是同步阻塞的，一旦它卡住，stateLock 就永远不释放，
    // 之后所有需要 stateLock 的 IPC 都会跟着卡死（2026-09-29 现场就是这个表现）。
    void PushInitialState()
    {
        ContextSnapshot snapshot;
        lock (stateLock) snapshot = BuildInitialSnapshot();
        SendWindow("state", snapshot);
    }

    ContextSnapshot BuildInitialSnapshot()
    {
        {
            var items = new List<ContextItem>();
            var storageRoot = FindStorageRoot();
            ContextTrace.Configure(storageRoot);
            var characters = GetCharacters();
            var activities = chatActivitySystem.GetAllChatActivities();
            var activityMap = activities.ToDictionary(a => a.Character.Name, a => a);

            // 初始快照：
            // - 所有角色：仅角色卡角色提示（基本信息，来自 index.json）
            // - 仅“当前实时（已激活）角色”：读取有界的实时 ChatHistory 上下文
            // 未激活角色的 History/记忆/附件一律不预读，前端进入该角色时通过 character:load 按需加载。
            //
            // 这里给**全文**（previewChars 传 0），不再只给 220 字预览：
            // 报文多大由 FillToBudget 按预算决定，装不下的条目会被标 Truncated 并在界面提示
            // 「点击读取全文」（走已有的 item:load）。所以「给全文」不会让报文变大，
            // 只会让**装得下的那些**显示完整 —— 这正是用户要的。
            foreach (var character in characters)
            {
                var owner = character.Name;
                items.AddRange(BuildOwnerItems(character, owner, storageRoot, activityMap, fullContent: true));
            }
            if (storageRoot != null) AddGlobalSources(storageRoot, items);

            // 摘要必须按**全部**条目算：条目本身会按预算分页装填（见下面的 FillToBudget），
            // 但「这个角色一共多少条 / 多少 token」必须是全量，否则界面统计会随着
            // 「还有 N 条未加载」一起偏小。
            var summaryHost = new ContextSnapshot { Items = items };
            BuildLiveSummaries(summaryHost, activityMap);
            var snapshot = new ContextSnapshot
            {
                Owners = summaryHost.Owners,
                Categories = summaryHost.Categories,
                Modalities = summaryHost.Modalities,
                MemoryLevels = summaryHost.MemoryLevels
            };
            // 只给「渲染进程正在查看的那一个角色」内联装配计划与提示词。
            // 依据（见 Resources/ContextManager/app.js）：
            //   - planJsonByOwner 只在 `snapshot.planJsonByOwner?.[selectedOwner]` 一处被读；
            //   - characterPromptByOwner / runtimeSystemByOwner 只是 nativePromptContent /
            //     runtimeSystemContent 的兜底，前端优先直接读磁盘 index.json；
            //   - 而且前端在缺计划时本来就会发 plan:get 兜底。
            // 以前给每个已激活角色都内联一份：两个角色时 state 涨到 216,535 字符
            // （写到 socket 上约 850 KB），把 IPC 桥顶断，整程序卡死。详见 ContextIpcBridge。
            var focused = ResolveFocusedOwner(activityMap);
            if (focused != null)
            {
                var owner = focused;
                // The live system message must not depend on plan-file IO.  A corrupt
                // or missing saved plan previously made a perfectly valid index[0]
                // appear as "not generated" in the inspector.
                var activeCharacter = activityMap[owner].Character;
                // CharacterSystem can retain an older Character instance after index.json
                // changes.  The editor must display the on-disk Prompt that it edits.
                var diskCharacter = ReadActiveCharacterFromDisk(activeCharacter, storageRoot);
                snapshot.CharacterPromptByOwner[owner] = diskCharacter.Prompt ?? "";
                var runtimeSystem = activityMap[owner].ChatBot.ChatHistory.FirstOrDefault(message => message.Role == AuthorRole.System);
                snapshot.RuntimeSystemByOwner[owner] = !string.IsNullOrWhiteSpace(runtimeSystem?.Content)
                    ? runtimeSystem.Content
                    : BuildOfficialSystemPrompt(diskCharacter, storageRoot);
                try
                {
                    var plan = GetPlanService().GetOrCreatePlan(owner);
                    FillNativePlanPrompt(plan, diskCharacter, storageRoot);
                    FillRuntimeSystemModuleContents(activityMap[owner], owner, items);
                    // This value is parsed directly by the renderer; keep the same
                    // camelCase wire shape as ContextIpcBridge.SendPlan.
                    // 走网线用紧凑 JSON：缩进版会把这个字符串撑大 30%+，而它会被外层
                    // 再转义一遍，是整个 state 报文里最大的一块。
                    InlinePlanIfSmall(snapshot, owner, plan);
                    ContextTrace.Write($"plan ok owner={owner} modules={plan.Modules.Count} mode={plan.Mode}");
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "初始状态未能读取上下文计划 {Owner}", owner);
                    ContextTrace.Write($"plan FAILED owner={owner}: {ex.GetType().Name}: {ex.Message}");
                    // 计划服务不可用时也必须给出可用计划，否则装配页会停在“计划未返回”。
                    try
                    {
                        var fallback = new ContextPlan
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
                                    Group = "system",
                                    Role = "System",
                                    Content = BuildOfficialSystemPrompt(diskCharacter, storageRoot),
                                    Enabled = true
                                }
                            }
                        };
                        InlinePlanIfSmall(snapshot, owner, fallback);
                        ContextTrace.Write($"plan fallback delivered owner={owner}");
                    }
                    catch (Exception inner) { ContextTrace.Write("plan fallback failed: " + inner.Message); }
                }
            }
            snapshot.Diagnostics = new DiagnosticsInfo
            {
                BaseDirectory = AppContext.BaseDirectory,
                StorageRoot = storageRoot ?? "",
                GeneratedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                AppBuild = AppBuild,
                TotalItems = items.Count,
                TotalTokens = items.Sum(i => i.EstimatedTokens)
            };

            // ===== 分页装填 =====
            // 到这里 snapshot 里除了 items 之外的字段都填好了，先把它们量出来当「保留量」，
            // 剩下的预算才给条目。这样「一条 state 最多多大」由预算硬保证，
            // 与用户用了多久、攒了多少条无关 —— 光调大预算只是把爆点往后推。
            //
            // 优先级：当前查看角色的最新条目在前（用户最可能在看它们），其它角色其次。
            // 排序只用于「谁进得来」，展示顺序仍旧按 Owner/Category/Title，界面不会跳来跳去。
            //
            // 另外留一块固定额度给下面的 OmittedByOwner：它是 Apply 之后才能定稿的，
            // 但同样占报文，不能让它把预算顶出去。
            const int OmittedMapReserveBytes = 4096;
            var reserve = ContextStateBudget.Measure(snapshot) + OmittedMapReserveBytes;
            var priority = OrderForFill(items, focused);
            var (kept, omitted) = ContextStateBudget.FillToBudget(priority, StateBudgetBytes, reserve);
            snapshot.Items = kept
                .OrderBy(i => i.Owner, StringComparer.Ordinal)
                .ThenBy(i => i.Category, StringComparer.Ordinal)
                .ThenBy(i => i.Title, StringComparer.Ordinal)
                .ToList();
            snapshot.OmittedItems = omitted;
            snapshot.HiddenItems = 0;

            // 报文体量：出站报文过大是这套 IPC 桥最可能的卡死诱因，所以每次都记下来。
            // 注意这里算的是**转义后**的字节数而不是字符数 —— SocketIOClient 用
            // System.Text.Json 序列化，汉字会被写成 \uXXXX（6 字节），字符数会严重低估。
            ContextStateBudget.Apply(snapshot, StateBudgetBytes);

            // 分页条说的是「当前这个角色还有多少条没加载」，所以按角色拆开统计；
            // 用**最终**要发的集合做差集 —— FillToBudget 和 Apply 的第 4 步都可能丢条目。
            var sentIds = new HashSet<string>(snapshot.Items.Select(i => i.Id), StringComparer.Ordinal);
            var omittedByOwner = CountByOwner(items.Where(i => !sentIds.Contains(i.Id)));
            snapshot.OmittedByOwner = omittedByOwner;
            snapshot.OmittedItems = omittedByOwner.Values.Sum();

            var wireBytes = ContextStateBudget.Measure(snapshot);
            ContextTrace.Write(
                $"state push: storageRoot={storageRoot} characters={characters.Count} active={activityMap.Count} " +
                $"items={items.Count} sent={snapshot.Items.Count} omitted={snapshot.OmittedItems} " +
                $"plans={snapshot.PlanJsonByOwner.Count} focused={focused ?? "<none>"} " +
                $"bytes={wireBytes} budget={StateBudgetBytes} steps=[{string.Join(",", snapshot.DegradeSteps)}]");
            if (snapshot.Degraded)
                ContextTrace.Write("state degraded: " + ContextStateBudget.Describe(snapshot.DegradeSteps));
            return snapshot;
        }
    }

    /// <summary>
    /// 渲染进程当前在看的角色：优先用 focusedOwner（由任何带 owner 的 IPC 更新），
    /// 它不在已激活集合里（还没激活 / 刚被停用）时退回第一个已激活角色。
    /// </summary>
    string? ResolveFocusedOwner(Dictionary<string, ChatActivity> activityMap)
    {
        var hint = focusedOwner;
        if (!string.IsNullOrWhiteSpace(hint))
        {
            foreach (var key in activityMap.Keys)
                if (string.Equals(key, hint, StringComparison.OrdinalIgnoreCase)) return key;
        }
        return activityMap.Keys.OrderBy(k => k, StringComparer.Ordinal).FirstOrDefault();
    }

    /// <summary>
    /// 计划 JSON 足够小才内联进 state。计划里存的是各模块全文，某角色那份 33 个模块
    /// 就有 187 KB（转义后 320 KB）—— 内联进去等于把 state 直接顶爆。太大的话前端会
    /// 发现 planJsonByOwner 里没有当前角色，转走 plan:get（独立的一条报文，不跟 state 挤）。
    /// </summary>
    void InlinePlanIfSmall(ContextSnapshot snapshot, string owner, ContextPlan plan)
    {
        var planJson = JsonConvert.SerializeObject(plan, WirePlanJsonSettings);
        // 用转义后的口径：计划 JSON 里全是中文模块正文，按 UTF-8 算会低估近一倍，
        // 48 KB 的阈值实际会放行约 90 KB 的报文。
        var planBytes = ContextStateBudget.EscapedBytes(planJson);
        if (planBytes <= PlanJsonInlineLimit)
        {
            snapshot.PlanJsonByOwner[owner] = planJson;
            return;
        }
        ContextTrace.Write($"plan inline skipped owner={owner} planBytes={planBytes} limit={PlanJsonInlineLimit}（改由 plan:get 下发）");
    }

    public void ApplyConfig(ContextManagerConfig? c) => Config = c ?? new ContextManagerConfig();
    public void Release()
    {
        chatActivitySystem.Activated -= OnActivityActivated;
        chatActivitySystem.Deactivated -= OnFrameworkActivityDeactivated;
        autoAttachTimer?.Dispose(); autoAttachTimer = null;
        lock (historySubscriptionLock)
        {
            foreach (var activity in chatActivitySystem.GetAllChatActivities())
                if (historySubscriptions.Remove(activity.ChatBot, out var callback)) activity.ChatBot.ChatHistoryEdited -= callback;
            historyRefreshTimer?.Dispose(); historyRefreshTimer = null;
        }
        // ⚠️ 必须把挂在 LanguageModel.ContextTransform 上的委托摘掉。
        //
        // 那个委托是闭包，捕获了 activity 和本实例（this）。插件被重载时 OnDestroy → Release，
        // 如果模型对象活得比插件长（重载插件而不重启 Alife），旧委托就留在模型上：
        //   1) 新运行时的 AttachTransform 会因为 `property.GetValue(model) != null` 直接返回，
        //      插件覆盖从此静默失效（trace 里只会看到 attached=false）；
        //   2) 旧运行时被这个闭包引用着，回收不掉 —— 每重载一次泄漏一份。
        // 原来 DetachTransforms() 写了却没人调用（见 ContextAssemblyRuntime.cs）。
        try { DetachTransforms(); } catch (Exception ex) { ContextTrace.Write("detach transforms failed: " + ex.Message); }
        current = null; windowService?.Dispose(); windowService = null;
    }

    void OnRendererMessage(string type, JsonElement payload) => HandleAsync(type, payload);

    async Task HandleAsync(string type, JsonElement payload = default)
    {
        ContextTrace.Write("<- " + type);
        // 记住渲染进程当前在看的角色：state 里只有它需要内联计划 JSON 与提示词全文。
        // 注意 JsonString 对非对象会抛异常，所以必须先判 ValueKind。
        if (payload.ValueKind == JsonValueKind.Object)
        {
            var hint = JsonString(payload, "owner") ?? JsonString(payload, "name");
            if (!string.IsNullOrWhiteSpace(hint)) focusedOwner = hint;
        }
        try
        {
            if (type == "context:refresh") QueueInitialState();
            else if (type == "worldbook:get") GetWorldBook(JsonString(payload, "owner") ?? "");
            else if (type == "worldbook:save") SaveWorldBook(JsonString(payload, "owner") ?? "", payload);
            else if (type == "worldbook:import") ImportWorldBook(JsonString(payload, "owner") ?? "");
            else if (type == "worldbook:page") GetWorldBookPage(JsonString(payload, "owner") ?? "", payload);
            else if (type == "worldbook:entry") GetWorldBookEntry(JsonString(payload, "owner") ?? "", payload);
            else if (type == "tavern:import") ImportTavernCard();
            else if (type == "card-worldbook-answer") AnswerUiAsk(payload);
            else if (type == "import:options") SendImportOptions(JsonString(payload, "owner") ?? "");
            else if (type == "import:options-save") SaveImportOptionsFromUi(JsonString(payload, "owner") ?? "", payload);
            else if (type == "tavern:import-preset") ImportTavernPreset();
            else if (type == "character:activate") ActivateCharacter(JsonString(payload, "owner") ?? "");
            else if (type == "character:deactivate") DeactivateCharacter(JsonString(payload, "owner") ?? "");
            else if (type == "chat:send") SendChat(JsonString(payload, "owner") ?? "", JsonString(payload, "content") ?? "");
            else if (type == "chat:prune") PruneUnassembledChat(JsonString(payload, "owner") ?? "", payload);
            else if (type == "history:page") QueryHistoryPage(JsonString(payload, "owner") ?? "", payload);
            else if (type == "presets:list") ListPresets();
            else if (type == "presets:get") GetPreset(JsonString(payload, "name") ?? "");
            else if (type == "presets:save") SavePreset(JsonString(payload, "name") ?? "", payload);
            else if (type == "presets:delete") DeletePreset(JsonString(payload, "name") ?? "");
            else if (type == "charpreset:list") ListCharacterPresets(JsonString(payload, "owner") ?? "");
            else if (type == "charpreset:save") SaveCharacterPresetFromUi(JsonString(payload, "owner") ?? "", payload);
            else if (type == "charpreset:load") LoadCharacterPreset(JsonString(payload, "owner") ?? "", JsonString(payload, "name") ?? "", false);
            else if (type == "charpreset:apply") LoadCharacterPreset(JsonString(payload, "owner") ?? "", JsonString(payload, "name") ?? "", true);
            else if (type == "charpreset:delete") DeleteCharacterPreset(JsonString(payload, "owner") ?? "", JsonString(payload, "name") ?? "");
            else if (type == "charpreset:export") ExportCharacterPreset(JsonString(payload, "owner") ?? "", payload);
            else if (type == "charpreset:import") ImportCharacterPreset(JsonString(payload, "owner") ?? "");
            else if (type == "character:load") LoadCharacterBundle(JsonString(payload, "owner") ?? JsonString(payload, "name") ?? "", payload.TryGetProperty("requestId", out var bundleRequestId) ? bundleRequestId.GetInt32() : 0);
            else if (type == "items:page") LoadItemPage(JsonString(payload, "owner") ?? "", JsonInt(payload, "offset"), JsonInt(payload, "count"));
            else if (type == "character:fields") GetCharacterFields(JsonString(payload, "name") ?? "");
            else if (type == "native-prompt:get") GetNativePrompt(JsonString(payload, "owner") ?? "");
            else if (type == "character:save") SaveCharacterFields(payload);
            else if (type == "character:create") CreateCharacterFromUi(JsonString(payload, "name") ?? "");
            else if (type == "item:load")
            {
                var id = JsonString(payload, "id");
                if (!string.IsNullOrWhiteSpace(id)) LoadFullItem(id);
            }
            else if (type == "item:update") UpdateItem(payload);
            else if (type == "item:delete") DeleteItem(payload);
            else if (type == "item:create") CreateItem(payload);
            else if (type == "plan:get") GetPlan(JsonString(payload, "owner") ?? "");
            else if (type == "plan:save") SavePlanFromUi(JsonString(payload, "owner") ?? "", payload);
            else if (type == "plan:preview") PreviewPlan(JsonString(payload, "owner") ?? "", payload);
            else if (type == "plan:apply") ApplyPlan(JsonString(payload, "owner") ?? "", payload);
            else if (type == "plan:import") ImportPlan(JsonString(payload, "owner") ?? "", payload);
            else if (type == "plan:import-file") ImportPlanFile(JsonString(payload, "owner") ?? "", payload);
            else if (type == "plan:module") GetPlanModule(JsonString(payload, "owner") ?? "", payload);
            else if (type == "plan:reset") ResetPlan(JsonString(payload, "owner") ?? "");
            else if (type == "plan:default") MakeDefaultPlan(JsonString(payload, "owner") ?? "");
            else if (type == "window:minimize") windowService?.Minimize();
            else if (type == "window:toggle-maximize") windowService?.ToggleMaximize();
            else if (type == "window:close") windowService?.Close();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "ContextManager 消息处理失败 {Type}", type);
            ContextTrace.Write("!! handle " + type + " failed: " + ex.Message);
            SendWindow("error", new { message = ex.Message });
        }
        await Task.CompletedTask;
    }

    // 只入队，不发送。`-> type` 由出站线程在真正写 socket 前记录，
    // 这样日志能证明「发送动作发生了」，而不是只证明「有人想发」。
    void SendWindow(string type, object? payload = null) => windowService?.Send(type, payload);
    public void PushSnapshot() => QueueInitialState();

    // ⚠️ 当前**没有任何调用点**：窗口走的一直是 QueueInitialState → PushInitialState
    // （BuildInitialSnapshot）。这里保留是因为它是「给所有角色都建条目」的另一种取舍，
    // 将来若要加"全部角色一览"会用到；但在改预算/降级逻辑时别把它当成生效路径 ——
    // 项目里已经有过两次「死代码骗人」的教训（AddOfflinePromptSummary、
    // Config.MaxPreviewChars），所以标注一下。
    void PushLiveState()
    {
        ContextSnapshot snapshot;
        lock (stateLock) snapshot = BuildLiveSnapshot();
        SendWindow("state", snapshot);
    }

    ContextSnapshot BuildLiveSnapshot()
    {
        {
        var items = new List<ContextItem>();
        var storageRoot = FindStorageRoot();
        var characters = GetCharacters();
            var activities = chatActivitySystem.GetAllChatActivities();
            var activityMap = activities.ToDictionary(a => a.Character.Name, a => a);
            foreach (var activity in activityMap.Values) SubscribeHistory(activity);

        // 所有角色都显示：
        // - 已激活：显示实时 ChatHistory（真实发送给 API 的完整上下文和多模态 Items）
        // - 未激活：只显示角色卡/index.json 可重建的人设提示词，不扫描大 History 文件
        foreach (var character in characters)
        {
            var owner = character.Name;
            if (activityMap.TryGetValue(owner, out var activity))
            {
                AddEditableCharacterPrompt(character, owner, storageRoot, items);
                AddLiveContext(activity, owner, IsMemoryEnabled(character), items);
                AddArchivedMemory(character, owner, storageRoot, items);
            }
            else
            {
                AddEditableCharacterPrompt(character, owner, storageRoot, items);
                AddOfflinePrompt(character, owner, storageRoot, items);
                AddOfflineHistory(character, owner, ResolveCharacterDirectory(character, storageRoot), items);
                AddArchivedMemory(character, owner, storageRoot, items);
            }
        }

        var snapshot = new ContextSnapshot
        {
            Items = items
                .OrderBy(i => i.Owner)
                .ThenBy(i => i.Title)
                .ToList()
        };
        BuildLiveSummaries(snapshot, activityMap);
        snapshot.Diagnostics = new DiagnosticsInfo
        {
            BaseDirectory = AppContext.BaseDirectory,
            StorageRoot = storageRoot ?? "",
            GeneratedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            TotalItems = items.Count,
            TotalTokens = items.Sum(i => i.EstimatedTokens)
        };
        // 这条路径会给「所有角色」建条目（未激活角色还会扫 History），报文很容易超限，
        // 所以同样过一遍预算，并记下真实字节数。
        ContextStateBudget.Apply(snapshot, StateBudgetBytes);
        ContextTrace.Write($"state push(live): active={activityMap.Count} items={items.Count} bytes={ContextStateBudget.Measure(snapshot)} steps=[{string.Join(",", snapshot.DegradeSteps)}]");
        return snapshot;
        }
    }

    void BuildLiveSummaries(ContextSnapshot snapshot, Dictionary<string, ChatActivity> activities)
    {
        var allCharacters = GetCharacters();
        snapshot.Owners = allCharacters.Select(c =>
        {
            activities.TryGetValue(c.Name, out var activity);
            var ownerItems = snapshot.Items.Where(i => i.Owner == c.Name).ToList();
            return new OwnerSummary
            {
                Name = c.Name,
                Count = ownerItems.Count,
                Tokens = ownerItems.Sum(i => i.EstimatedTokens),
                Active = activity != null,
                MemoryEnabled = IsMemoryEnabled(c)
            };
        }).OrderByDescending(o => o.Tokens).ToList();

        snapshot.Categories = snapshot.Items.GroupBy(i => i.Category).Select(g => new CategorySummary
        {
            Name = g.Key, Count = g.Count(), Tokens = g.Sum(i => i.EstimatedTokens)
        }).OrderByDescending(c => c.Tokens).ToList();

        snapshot.Modalities = snapshot.Items.GroupBy(i => i.Modality).Select(g => new ModalitySummary
        {
            Name = g.Key, Count = g.Count(), Tokens = g.Sum(i => i.EstimatedTokens)
        }).OrderByDescending(m => m.Count).ToList();

        snapshot.MemoryLevels = snapshot.Items
            .Where(i => i.MemoryLevel > 0)
            .GroupBy(i => i.MemoryLevel)
            .Select(g => new MemoryLevelInfo { Level = g.Key, Count = g.Count(), Tokens = g.Sum(i => i.EstimatedTokens) })
            .OrderBy(i => i.Level).ToList();
    }

    bool IsMemoryEnabled(Character character)
    {
        try
        {
            var type = moduleSystem.GetModule(MemoryModuleId);
            return type != null && character.Modules.Contains(MemoryModuleId);
        }
        catch { return false; }
    }

    void AddLiveContext(ChatActivity activity, string owner, bool memoryEnabled, List<ContextItem> items, int previewChars = 0)
    {
        var history = activity.ChatBot.ChatHistory;
        var index = 0;
        foreach (var message in history)
        {
            var role = message.Role.Label;
            var isSystem = message.Role == AuthorRole.System;
            var attachments = ExtractAttachments(message, includeBinary: false);
            var text = message.Content ?? "";
            int level = 0;
            var title = isSystem
                ? LiveSystemTitle(text, index)
                : $"{role} #{index}";
            var category = isSystem
                ? "功能说明"
                : (TryParseMemory(text, out level)
                    ? "长期记忆"
                    : "实时上下文");

            var modality = attachments.Count > 0
                ? (text.Length > 0 ? "multimodal" : attachments[0].Modality)
                : DetectTextModality(text);

            var item = new ContextItem
            {
                Category = category,
                Kind = role,
                Modality = modality,
                Owner = owner,
                Title = title,
                Content = previewChars > 0 ? PreviewLimit(text, previewChars) : Limit(text),
                Truncated = previewChars > 0 && text.Length > previewChars,
                Source = isSystem ? "角色卡/系统装配" : "实时 ChatBot.ChatHistory",
                SourceKey = isSystem ? "live-system" : "live-history",
                Origin = "live",
                EstimatedTokens = EstimateTokens(text) + attachments.Sum(a => 120),
                MemoryLevel = level,
                Attachments = attachments,
                Tags = BuildLiveTags(role, memoryEnabled, category),
                UpdatedAt = DateTime.Now
            };
            item.Id = StableId(owner, "live", role, index.ToString());
            items.Add(item);
            index++;
        }
    }

    List<string> BuildLiveTags(string role, bool memoryEnabled, string category)
    {
        var tags = new List<string> { role, "完整API上下文" };
        if (category == "长期记忆") tags.Add("记忆存档");
        tags.Add(memoryEnabled ? "MemoryService:开" : "MemoryService:关");
        return tags;
    }

    List<ContextAttachment> ExtractAttachments(ChatMessageContent message, bool includeBinary = true)
    {
        var result = new List<ContextAttachment>();
        if (message.Items == null) return result;

        foreach (var item in message.Items)
        {
            switch (item)
            {
                case ImageContent img:
                    result.Add(BuildMediaAttachment("image", img.Uri, includeBinary ? img.DataUri : null, img.MimeType));
                    break;
                case AudioContent aud:
                    result.Add(BuildMediaAttachment("audio", aud.Uri, includeBinary ? aud.DataUri : null, aud.MimeType));
                    break;
                case FileReferenceContent file:
                    result.Add(new ContextAttachment
                    {
                        Modality = "file",
                        Title = file.FileId ?? "文件引用",
                        Status = "reference",
                        Detail = file.FileId ?? "",
                        Path = ""
                    });
                    break;
                case TextContent text:
                    // ⚠️ 这里以前是 Detail = Limit(text.Text)，也就是把**整条消息正文**
                    // 又复制一份塞进附件里。后果有两层，都是 2026-09-30 现场量出来的：
                    //   1. 每条消息的正文在报文里出现两次（Content 里一份预览、
                    //      Attachments[0].Detail 里一份全文），某角色 114 条消息白多 219 KB，
                    //      character-bundle 因此涨到 635 KB 被硬上限拦掉，角色加载直接失败；
                    //   2. 更糟的是降级阶梯只清 Content、不动 Attachments，
                    //      所以「items-content-dropped」之后正文其实还在 —— 降级是假的。
                    // 附件里的「文本片段」只需要一个可辨识的短预览；正文本身在 Content 里。
                    if (!string.IsNullOrWhiteSpace(text.Text))
                        result.Add(new ContextAttachment { Modality = "text", Title = "文本片段", Detail = ShortPreview(text.Text) });
                    break;
            }
        }
        return result;
    }

    string? JsonString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) ? value.GetString() : null;

    /// <summary>安全地取一个整数参数：字段缺失、类型不对或不是数字都返回 0，不抛异常。</summary>
    static int JsonInt(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.Number
           && value.TryGetInt32(out var number) ? number : 0;

    static string StableId(params string?[] parts)
    {
        var joined = string.Join("|", parts.Select(p => p ?? ""));
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(joined));
        return Convert.ToHexString(bytes).Substring(0, 16).ToLowerInvariant();
    }

    void UpdateItem(JsonElement payload)
    {
        var id = JsonString(payload, "id");
        var content = JsonString(payload, "content");
        if (id == null || content == null) throw new InvalidOperationException("缺少 id 或 content");

        var snapshot = BuildMetadataSnapshotOnly();
        var item = snapshot.Items.FirstOrDefault(i => i.Id == id);
        // 离线加载的 archive 条目不在 BuildMetadataSnapshotOnly 中，从前端 payload 补充元数据
        if (item == null)
        {
            var path = JsonString(payload, "path");
            var sourceKey = JsonString(payload, "sourceKey");
            var owner = JsonString(payload, "owner");
            if (!string.IsNullOrEmpty(path) && sourceKey?.StartsWith("archive-L") == true && File.Exists(path))
            {
                File.WriteAllText(path, content);
                QueueInitialState();
                return;
            }
            throw new InvalidOperationException("条目不存在");
        }
        // 只对真正的只读快照类型拦截：离线系统装配、被截断的分页、磁盘媒体文件
        if (item.ReadOnly && item.SourceKey is "offline-system" or "offline-history-truncated" or "local-media")
            throw new InvalidOperationException("该条目为系统装配快照，只读；请编辑对应的原始 Prompt 或文本文件");

        switch (item.SourceKey)
        {
            case "character-prompt":
                UpdateIndexJson(item.Owner, jo => jo["Prompt"] = content);
                break;
            case "offline-history":
                UpdateHistoryContent(item, content);
                break;
            case "live-system":
            case "live-history":
                ApplyLiveContentEdit(item, content);
                break;
            default:
                if (item.SourceKey?.StartsWith("archive-L") == true)
                {
                    if (string.IsNullOrWhiteSpace(item.Path) || !File.Exists(item.Path))
                        throw new FileNotFoundException("归档文件不存在", item.Path);
                    File.WriteAllText(item.Path, content);
                }
                else throw new InvalidOperationException("暂不支持编辑该类型条目");
                break;
        }

        // 角色原始设定和磁盘历史在角色激活时需要额外同步；live-system/live-history 已在上方直接写入。
        if (item.SourceKey is not "live-system" and not "live-history")
            ApplyLiveContentEdit(item, content);
        QueueInitialState();
    }

    void DeleteItem(JsonElement payload)
    {
        var id = JsonString(payload, "id");
        if (id == null) throw new InvalidOperationException("缺少 id");
        var snapshot = BuildMetadataSnapshotOnly();
        var item = snapshot.Items.FirstOrDefault(i => i.Id == id);
        // 离线加载的 archive 条目不在 BuildMetadataSnapshotOnly 中，从前端 payload 补充
        if (item == null)
        {
            var path = JsonString(payload, "path");
            var sourceKey = JsonString(payload, "sourceKey");
            if (!string.IsNullOrEmpty(path) && sourceKey?.StartsWith("archive-L") == true && File.Exists(path))
            {
                File.Delete(path);
                QueueInitialState();
                return;
            }
            throw new InvalidOperationException("条目不存在");
        }
        // 只对真正的只读快照类型拦截删除
        if (item.ReadOnly && item.SourceKey is "offline-system" or "offline-history-truncated" or "local-media")
            throw new InvalidOperationException("系统装配快照不能直接删除");

        if (item.SourceKey == "offline-history")
        {
            DeleteHistoryItem(item);
            RemoveLiveMessage(item);
        }
        else if (item.SourceKey?.StartsWith("archive-L") == true)
        {
            if (File.Exists(item.Path)) File.Delete(item.Path);
        }
        else if (item.SourceKey == "character-prompt")
        {
            throw new InvalidOperationException("角色原始设定不能删除；可以清空或修改内容");
        }
        else throw new InvalidOperationException("暂不支持删除该类型条目");

        QueueInitialState();
    }

    void CreateItem(JsonElement payload)
    {
        var owner = JsonString(payload, "owner");
        var kind = JsonString(payload, "kind") ?? "note";
        var content = JsonString(payload, "content") ?? "";
        var role = JsonString(payload, "role") ?? "user";
        if (string.IsNullOrWhiteSpace(owner)) throw new InvalidOperationException("请选择角色");
        if (FindActivity(owner) == null) throw new InvalidOperationException("角色未激活，不能创建本地上下文条目");

        if (kind == "archive")
        {
            var character = GetCharacters().FirstOrDefault(c => c.Name == owner)
                ?? throw new InvalidOperationException("角色不存在");
            var dir = ResolveCharacterDirectory(character, FindStorageRoot()) ?? throw new InvalidOperationException("角色目录不存在");
            var levelDir = Path.Combine(dir, "Memory", "L1");
            Directory.CreateDirectory(levelDir);
            var file = Path.Combine(levelDir, $"manual-{DateTime.Now:yyyyMMddHHmmss}.txt");
            File.WriteAllText(file, content);
        }
        else
        {
            AppendHistoryItem(owner, role, content);
            AppendLiveMessage(owner, role, content);
        }

        QueueInitialState();
    }

    ContextSnapshot BuildSnapshotWithoutPush()
    {
        var items = new List<ContextItem>();
        var storageRoot = FindStorageRoot();
        var characters = GetCharacters();
        var activities = chatActivitySystem.GetAllChatActivities();
        var activityMap = activities.ToDictionary(a => a.Character.Name, a => a);
        foreach (var character in characters)
        {
            var owner = character.Name;
            if (activityMap.TryGetValue(owner, out var activity))
            {
                AddEditableCharacterPrompt(character, owner, storageRoot, items);
                AddLiveContext(activity, owner, IsMemoryEnabled(character), items);
                AddArchivedMemory(character, owner, storageRoot, items);
            }
            else
            {
                AddEditableCharacterPrompt(character, owner, storageRoot, items);
                AddOfflinePrompt(character, owner, storageRoot, items);
                AddOfflineHistory(character, owner, ResolveCharacterDirectory(character, storageRoot), items);
                AddArchivedMemory(character, owner, storageRoot, items);
            }
        }
        AddGlobalSources(storageRoot, items);
        var snapshot = new ContextSnapshot { Items = items };
        BuildLiveSummaries(snapshot, activityMap);
        return snapshot;
    }

    Character GetCharacterRequired(string owner)
        => GetCharacters().FirstOrDefault(c => c.Name == owner) ?? throw new InvalidOperationException($"角色不存在：{owner}");




    void LoadCharacterBundle(string owner, int requestId = 0)
    {
        _ = Task.Run(() =>
        {
            List<ContextItem> bundle;
            var inactive = false;
            try
            {
                var storageRoot = FindStorageRoot();
                var character = GetCharacterRequired(owner);
                var activityMap = chatActivitySystem.GetAllChatActivities()
                    .ToDictionary(a => a.Character.Name, a => a);
                // 读盘 + 构建放在锁内（需要一致的元数据），发送放到锁外：见 PushInitialState 的说明。
                lock (stateLock)
                {
                    if (!activityMap.ContainsKey(owner))
                    {
                        // Strict activation-only policy: return no local context for inactive owners.
                        inactive = true;
                        bundle = new List<ContextItem>();
                    }
                    else
                    {
                        bundle = BuildOwnerItems(character, owner, storageRoot, activityMap, fullContent: true);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "按需加载角色上下文失败 {Owner}", owner);
                SendWindow("error", new { message = "加载角色上下文失败：" + ex.Message });
                return;
            }

            // 与 state 同一套预算装填：bundle 也是「一条报文」，同样不能无限大。
            // 2026-09-30 现场它涨到 635 KB 被硬上限拦掉，界面直接报「体积过大」、
            // 角色加载失败。装不下的条目由前端提示并走 items:page。
            //
            // 排序必须与 state / items:page 完全一致（OrderForFill），否则前端传回来的
            // 「已持有 N 条」这个 offset 会对不上，翻页时漏条或重复。
            var (kept, omitted) = ContextStateBudget.FillToBudget(
                OrderForFill(bundle, owner), StateBudgetBytes, 0);
            ContextTrace.Write($"character-bundle owner={owner} total={bundle.Count} sent={kept.Count} omitted={omitted} inactive={inactive}");
            SendWindow("character-bundle", new { owner, requestId, inactive, items = kept, omitted });
        });
    }

    /// <summary>
    /// 分页取某个角色的条目（state / character-bundle 装不下的那部分）。
    ///
    /// ⚠️ 顺序**必须**与 state 的装填顺序完全一致（同一个 OrderForFill）。
    /// 前端把「已经拿到多少条」当作 offset 传回来，所以这个顺序一旦变了就会漏条或重复
    /// ——用户会看到某些消息永远翻不出来。focused 传 owner 本身即可：
    /// 同一个角色内部的相对顺序只由 FillTier + UpdatedAt 决定，与当前查看谁无关。
    /// </summary>
    void LoadItemPage(string owner, int offset, int count)
    {
        _ = Task.Run(() =>
        {
            if (string.IsNullOrWhiteSpace(owner))
            {
                SendWindow("error", new { message = "分页加载缺少角色名" });
                return;
            }
            if (count <= 0) count = 40;
            if (count > 120) count = 120;

            List<ContextItem> page;
            int total;
            try
            {
                lock (stateLock)
                {
                    var all = BuildOwnerItems(owner, FindStorageRoot(), fullContent: true);
                    var ordered = OrderForFill(all, owner);
                    total = ordered.Count;
                    page = ordered.Skip(Math.Max(0, offset)).Take(count).ToList();
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "分页加载条目失败 {Owner}", owner);
                SendWindow("error", new { message = "分页加载失败：" + ex.Message });
                return;
            }

            var (kept, dropped) = ContextStateBudget.FillToBudget(page, StateBudgetBytes, 0);
            // remaining 由后端算：后端才知道这个角色**一共**多少条，
            // 前端拿 total 减自己持有的数量容易被截断/去重影响而算错。
            var start = Math.Max(0, offset);
            var remaining = Math.Max(0, total - (start + kept.Count));
            ContextTrace.Write($"items:page owner={owner} offset={offset} total={total} sent={kept.Count} dropped={dropped} remaining={remaining}");
            SendWindow("items-page", new
            {
                owner, offset, total, remaining,
                // nextOffset 直接由「这次服务了哪一段」推出，前端下次原样传回来即可。
                // 让前端自己数「已持有多少条」会被去重影响，一旦对不上就会翻页漏条。
                nextOffset = start + kept.Count,
                items = kept
            });
        });
    }

    void GetCharacterFields(string name)
    {
        _ = Task.Run(() =>
        {
            try
            {
                var character = GetCharacterRequired(name);
                var dir = ResolveCharacterDirectory(character, FindStorageRoot()) ?? throw new InvalidOperationException("角色目录不存在");
                var path = Path.Combine(dir, "index.json");
                var jo = JObject.Parse(File.ReadAllText(path));
                var birthday = jo.Value<DateTime?>("Birthday");
                SendWindow("character-fields", new
                {
                    name,
                    birthday = birthday?.ToString("yyyy-MM-ddTHH:mm"),
                    description = jo.Value<string>("Description") ?? ""
                });
            }
            catch (Exception ex) { SendWindow("error", new { message = "读取角色字段失败：" + ex.Message }); }
        });
    }

    void SaveCharacterFields(JsonElement payload)
    {
        var owner = JsonString(payload, "name") ?? JsonString(payload, "owner")
            ?? throw new InvalidOperationException("缺少角色名");
        var character = GetCharacterRequired(owner);
        if (FindActivity(owner) == null) throw new InvalidOperationException("角色未激活，不能保存本地上下文字段");
        var dir = ResolveCharacterDirectory(character, FindStorageRoot()) ?? throw new InvalidOperationException("角色目录不存在");
        var path = Path.Combine(dir, "index.json");
        var jo = JObject.Parse(File.ReadAllText(path));

        if (payload.TryGetProperty("birthday", out var birthdayElement))
        {
            var raw = birthdayElement.ValueKind == JsonValueKind.String ? birthdayElement.GetString() : null;
            if (!string.IsNullOrWhiteSpace(raw) && DateTime.TryParse(raw, out var birthday))
                jo["Birthday"] = birthday;
        }
        if (payload.TryGetProperty("description", out var descriptionElement))
            jo["Description"] = descriptionElement.ValueKind == JsonValueKind.String ? descriptionElement.GetString() ?? "" : "";
        if (payload.TryGetProperty("prompt", out var promptElement))
            jo["Prompt"] = promptElement.ValueKind == JsonValueKind.String ? promptElement.GetString() ?? "" : "";

        File.WriteAllText(path, jo.ToString(Formatting.Indented));
        SendWindow("character-saved", new { name = owner });
        QueueInitialState();
    }

    void UpdateIndexJson(string owner, Action<JObject> update)
    {
        var character = GetCharacterRequired(owner);
        var dir = ResolveCharacterDirectory(character, FindStorageRoot()) ?? throw new InvalidOperationException("角色目录不存在");
        var path = Path.Combine(dir, "index.json");
        var jo = JObject.Parse(File.ReadAllText(path));
        update(jo);
        File.WriteAllText(path, jo.ToString(Formatting.Indented));
    }

    string GetHistoryPath(string owner)
    {
        var character = GetCharacterRequired(owner);
        var dir = ResolveCharacterDirectory(character, FindStorageRoot()) ?? throw new InvalidOperationException("角色目录不存在");
        var path = Path.Combine(dir, "Memory", "History.json");
        if (!File.Exists(path)) File.WriteAllText(path, "[]");
        return path;
    }

    void UpdateHistoryContent(ContextItem item, string content)
    {
        var path = GetHistoryPath(item.Owner);
        var arr = JArray.Parse(File.ReadAllText(path));
        // 按该角色历史条目顺序定位：Title 形如 user #3
        var index = ExtractHistoryIndex(item.Title);
        if (index < 0 || index >= arr.Count) throw new InvalidOperationException("无法定位 History.json 条目");
        arr[index]["Content"] = content;
        File.WriteAllText(path, arr.ToString(Formatting.Indented));
    }

    void DeleteHistoryItem(ContextItem item)
    {
        var path = GetHistoryPath(item.Owner);
        var arr = JArray.Parse(File.ReadAllText(path));
        var index = ExtractHistoryIndex(item.Title);
        if (index < 0 || index >= arr.Count) throw new InvalidOperationException("无法定位 History.json 条目");
        arr.RemoveAt(index);
        File.WriteAllText(path, arr.ToString(Formatting.Indented));
    }

    void AppendHistoryItem(string owner, string role, string content)
    {
        var path = GetHistoryPath(owner);
        var arr = JArray.Parse(File.ReadAllText(path));
        var now = DateTime.Now;
        var jo = new JObject
        {
            ["Role"] = new JObject { ["Label"] = role },
            ["Content"] = content,
            ["MemoryMeta"] = new JObject
            {
                ["Level"] = 0,
                ["StartTime"] = now,
                ["EndTime"] = now,
                ["Name"] = $"0-{now:yyyyMMddHHmmss}-{now:yyyyMMddHHmmss}"
            }
        };
        arr.Add(jo);
        File.WriteAllText(path, arr.ToString(Formatting.Indented));
    }

    static int ExtractHistoryIndex(string title)
    {
        var match = Regex.Match(title, @"#(\d+)$");
        return match.Success ? int.Parse(match.Groups[1].Value) : -1;
    }

    // 编辑「实时上下文 / 原始记忆流」条目 = 直接改写 ChatHistory 里那一条消息。
    // 这里以前失败是静默 return：用户点了保存、界面刷新后又变回旧内容，看起来就是
    // “原始记忆流不能修改”。现在改成抛出明确原因，让界面能说清为什么没写进去。
    void ApplyLiveContentEdit(ContextItem item, string content)
    {
        var activity = FindActivity(item.Owner);
        if (activity == null)
            throw new InvalidOperationException($"角色「{item.Owner}」当前未激活，无法改写实时上下文。请先激活该角色再编辑。");
        var index = item.SourceKey == "character-prompt"
            ? 0
            : ExtractHistoryIndex(item.Title);
        if (index < 0)
            throw new InvalidOperationException($"无法定位这条实时消息（标题「{item.Title}」里没有可用的序号）。");
        var written = false;
        activity.ChatBot.EditChatHistory(thread =>
        {
            if (index >= 0 && index < thread.ChatHistory.Count)
            {
                thread.ChatHistory[index].Content = content;
                written = true;
            }
        }, "ContextManager 编辑上下文");
        if (!written)
            throw new InvalidOperationException(
                $"实时上下文当前只有 {activity.ChatBot.ChatHistory.Count} 条，序号 #{index} 已经不存在（对话可能被压缩或截断过）。请刷新后重试。");
    }

    void RemoveLiveMessage(ContextItem item)
    {
        var activity = FindActivity(item.Owner);
        if (activity == null) return;
        var index = ExtractHistoryIndex(item.Title);
        if (index <= 0 || index >= activity.ChatBot.ChatHistory.Count) return;
        activity.ChatBot.EditChatHistory(thread =>
        {
            thread.ChatHistory.RemoveAt(index);
        }, "ContextManager 删除上下文");
    }

    void GetNativePrompt(string owner)
    {
        // Reply on the renderer's request thread.  Task.Run replies have been
        // dropped intermittently by this Electron.NET bridge.
        var activity = FindActivity(owner);
        if (activity == null) return;
        var character = ReadActiveCharacterFromDisk(activity.Character, FindStorageRoot());
        // system = 完整官方系统消息（名称/生日/简介/设定/私人文件夹）。装配页的「角色设定 #0」
        // 保存的就是它，所以前端需要它来“填入官方模板”。
        SendWindow("native-prompt", new { owner, prompt = character.Prompt ?? "", system = BuildOfficialSystemPrompt(character, FindStorageRoot()) });
    }

    void FillRuntimeSystemModuleContents(ChatActivity activity, string owner, List<ContextItem> items)
    {
        foreach (var item in items.Where(i => i.Owner == owner && i.SourceKey == "live-system"))
        {
            var index = ExtractHistoryIndex(item.Title);
            if (index >= 0 && index < activity.ChatBot.ChatHistory.Count)
                item.Content = activity.ChatBot.ChatHistory[index].Content ?? "";
        }
    }

    void PruneUnassembledChat(string owner, JsonElement payload)
    {
        try
        {
            var plan = PreparePlan(owner, payload);
            var requested = payload.TryGetProperty("indices", out var values) && values.ValueKind == JsonValueKind.Array
                ? values.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out _)).Select(v => v.GetInt32()).Distinct().OrderByDescending(i => i).ToList()
                : new List<int>();
            if (requested.Count == 0) throw new InvalidOperationException("没有待删除的未参与装配对话");
            var excluded = plan.Modules.Where(module => module.Source == "framework" && module.Group == "chat" && !module.Enabled)
                .Select(module => module.TargetIndex).ToHashSet();
            if (requested.Any(index => !excluded.Contains(index)))
                throw new InvalidOperationException("只能删除已取消参与装配的对话消息");
            lock (stateLock)
            {
                var activity = FindActivity(owner) ?? throw new InvalidOperationException("角色未激活，不能清理实时对话");
                var removable = requested.Where(i => i > 0 && i < activity.ChatBot.ChatHistory.Count && activity.ChatBot.ChatHistory[i].Role != AuthorRole.System).ToList();
                if (removable.Count == 0) throw new InvalidOperationException("没有可删除的 user/assistant 对话；系统消息不会被清理");
                activity.ChatBot.EditChatHistory(thread =>
                {
                    foreach (var index in removable) if (index < thread.ChatHistory.Count) thread.ChatHistory.RemoveAt(index);
                }, "ContextManager 批量清理未参与装配的对话");

                var service = GetPlanService();
                plan.Modules.RemoveAll(module => module.Source == "framework" && removable.Contains(module.TargetIndex));
                foreach (var module in plan.Modules.Where(module => module.Source == "framework" && module.TargetIndex >= 0))
                    module.TargetIndex -= removable.Count(index => index < module.TargetIndex);
                service.SavePlan(owner, plan);
                SendPlan(owner, plan);
                SendWindow("chat-pruned", new { owner, removed = removable.Count });
            }
            QueueInitialState();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "批量清理未参与装配的对话失败 {Owner}", owner);
            SendWindow("plan-error", new { owner, message = ex.Message });
        }
    }

    void AppendLiveMessage(string owner, string role, string content)
    {
        var activity = FindActivity(owner);
        if (activity == null) return;
        var message = new ChatMessageContent(new AuthorRole(role), content);
        activity.ChatBot.EditChatHistory(thread =>
        {
            thread.ChatHistory.Add(message);
        }, "ContextManager 新增上下文");
    }

    ChatActivity? FindActivity(string owner)
        => chatActivitySystem.GetAllChatActivities().FirstOrDefault(a => a.Character.Name == owner);

    // 实时上下文列表里每条正文的预览长度。
    // 以前这里是「临时把 Config.MaxPreviewChars 改成 220 再调 AddLiveContext」，
    // 但 AddLiveContext 用的是 Limit()，而 Limit() 无条件返回全文 ——
    // MaxPreviewChars 从来没被任何地方读过。也就是说所谓「短预览」其实一直在推送
    // 完整 ChatHistory：一个已激活角色的 54 条长记忆就能把 state 顶到 216,535 字符
    // （写到 socket 上约 850 KB），足以把 IPC 桥顶断。现在改成显式传参。
    const int LiveSummaryPreviewChars = 220;

    void AddLiveContextSummary(ChatActivity activity, string owner, bool memoryEnabled, List<ContextItem> items,
        int previewChars = LiveSummaryPreviewChars)
        => AddLiveContext(activity, owner, memoryEnabled, items, previewChars);

    /// <summary>
    /// 装填顺序的唯一真值来源在 ContextStateBudget.OrderForFill（放在那里是为了能单元测试）。
    /// 这里只是给运行时一个短名字，避免各处再抄一遍排序口径。
    /// </summary>
    static List<ContextItem> OrderForFill(IEnumerable<ContextItem> items, string? focused)
        => ContextStateBudget.OrderForFill(items, focused);

    /// <summary>把一批条目按 Owner 数一遍，用于「每个角色还有多少条没加载」。</summary>
    static Dictionary<string, int> CountByOwner(IEnumerable<ContextItem> items)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            map.TryGetValue(item.Owner, out var count);
            map[item.Owner] = count + 1;
        }
        return map;
    }

    /// <summary>
    /// 构建某个角色的**全部**条目：实时上下文 + 归档记忆 + 本地媒体。
    ///
    /// 抽出来是为了让 state 的分页装填、character-bundle、items:page 三处看到**同一批条目**
    /// 且顺序口径一致 —— 否则分页的 offset 会对不上，出现「加载更早」翻页时漏条或重复。
    ///
    /// 未激活角色返回空列表（strict activation-only：未激活就不预读本地上下文）。
    /// </summary>
    List<ContextItem> BuildOwnerItems(Character character, string owner, string? storageRoot,
        Dictionary<string, ChatActivity> activityMap, bool fullContent)
    {
        var items = new List<ContextItem>();
        if (!activityMap.TryGetValue(owner, out var activity)) return items;
        var previewChars = fullContent ? 0 : LiveSummaryPreviewChars;
        // ⚠️ 这里必须有 AddEditableCharacterPrompt。
        // 它是 sourceKey = "character-prompt" 那条**唯一**的来源：提示词工作室的文本框、
        // 角色管理页的角色摘要、装配页的「角色卡独立系统提示词」分组全都按它取数据。
        // 上一轮把它漏掉之后，这些地方全都读不到东西（界面直接空白），
        // 而它是 FillTier 的第 0 档、体积又小，本来就不该被分页挤出去。
        AddEditableCharacterPrompt(character, owner, storageRoot, items);
        var itemCount = items.Count;
        AddLiveContextSummary(activity, owner, IsMemoryEnabled(character), items, previewChars);
        if (items.Count == itemCount) AddLivePromptFallback(character, owner, items);
        FillRuntimeSystemModuleContents(activity, owner, items);
        AddArchivedMemorySummaries(character, owner, storageRoot, items);
        AddLocalMediaFilesForCharacter(character, storageRoot, items);
        return items;
    }

    /// <summary>按角色名构建条目（分页用）。角色不存在或未激活时返回空列表。</summary>
    List<ContextItem> BuildOwnerItems(string owner, string? storageRoot, bool fullContent)
    {
        var character = characterSystem.GetAllCharacters().FirstOrDefault(c => c.Name == owner);
        if (character == null) return new List<ContextItem>();
        var activityMap = chatActivitySystem.GetAllChatActivities()
            .ToDictionary(a => a.Character.Name, a => a);
        return BuildOwnerItems(character, owner, storageRoot, activityMap, fullContent);
    }

    static string LiveSystemTitle(string text, int index)
    {
        if (index == 0) return "角色设定 #0";
        var match = Regex.Match(text ?? "", @"^\[功能说明\(([^)]+)\)\]");
        return match.Success ? $"{match.Groups[1].Value} #{index}" : $"系统模块 #{index}";
    }

    void AddLivePromptFallback(Character character, string owner, List<ContextItem> items)
    {
        var prompt = character.Prompt ?? "";
        items.Add(new ContextItem
        {
            Id = StableId(owner, "live", "system", "fallback"),
            Category = "系统提示词",
            Kind = "system",
            Modality = "text",
            Owner = owner,
            Title = "角色设定",
            Content = Limit(prompt),
            Source = "活动角色 Prompt（ChatHistory 尚未建立）",
            SourceKey = "live-system",
            Origin = "live",
            EstimatedTokens = EstimateTokens(prompt),
            ReadOnly = false,
            Tags = new List<string> { "system", "角色设定", "运行时兜底" },
            UpdatedAt = DateTime.Now
        });
    }

    // 注意：这里以前有一个 AddOfflinePromptSummary()，体内写
    // `Config.MaxPreviewChars = Math.Min(backup, 220)` 再调 AddOfflinePrompt()。
    // 但全项目没有任何地方读 MaxPreviewChars，AddOfflinePrompt 用的也是 Limit()（全文），
    // 所以它既没有调用点、也没有任何效果 —— 只是让人误以为“离线预览是截断的”。
    // 已经删掉。真正控制预览长度的是下面这些显式传参的 *Summary 函数。

    void AddOfflineHistorySummaries(Character character, string owner, string? characterDir, List<ContextItem> items)
    {
        // 离线大文件：初始只索引前 200 条、每条 220 字预览；全文走 history:page / item:load。
        AddOfflineHistory(character, owner, characterDir, items, 200, 220);
    }

    void AddArchivedMemorySummaries(Character character, string owner, string? storageRoot, List<ContextItem> items)
    {
        var dir = ResolveCharacterDirectory(character, storageRoot);
        if (dir == null) return;
        var memoryRoot = Path.Combine(dir, "Memory");
        if (!Directory.Exists(memoryRoot)) return;
        foreach (var levelDir in Directory.GetDirectories(memoryRoot).OrderBy(d => d))
        {
            var levelText = Path.GetFileName(levelDir);
            if (!int.TryParse(levelText.TrimStart('L'), out var level)) continue;
            foreach (var file in Directory.GetFiles(levelDir, "*.txt").OrderBy(f => f))
            {
                var content = File.ReadAllText(file);
                items.Add(new ContextItem
                {
                    Category = "归档记忆文件",
                    Kind = "archive",
                    Modality = "text",
                    Owner = owner,
                    Title = Path.GetFileName(file),
                    Content = ShortPreview(content),
                    Path = file,
                    Source = $"Memory/{levelText}",
                    SourceKey = $"archive-L{level}",
                    Origin = "disk",
                    Size = new FileInfo(file).Length,
                    EstimatedTokens = EstimateTokens(content),
                    MemoryLevel = level,
                    ReadOnly = false,
                    Tags = new List<string> { levelText, "按需读取全文" },
                    Id = StableId(owner, "archive", file),
                    UpdatedAt = File.GetLastWriteTime(file)
                });
            }
        }
    }

    string ShortPreview(string text)
    {
        text ??= "";
        var normalized = text.Replace("\r", " ").Replace("\n", " ");
        return normalized.Length <= 180 ? text : normalized[..180] + "…";
    }

    void LoadFullItem(string id)
    {
        _ = Task.Run(() =>
        {
            ContextItem? item;
            // 读盘放在锁内（需要一致的元数据），发送放到锁外：见 PushInitialState 的说明。
            lock (stateLock)
            {
                var snapshot = BuildMetadataSnapshotOnly();
                item = snapshot.Items.FirstOrDefault(i => i.Id == id);
                if (item != null)
                {
                    item.Content = ReadFullContent(item);
                    // 拿到全文了，清掉「这是预览」的标记。否则渲染进程收到的 item 会带着
                    // 陈旧的 truncated=true，界面上会被误判成还没加载全文。
                    item.Truncated = false;
                    FillLiveAttachments(item);
                }
            }
            if (item == null) { SendWindow("error", new { message = "条目不存在" }); return; }
            SendWindow("item-state", new { item });
        });
    }

    ContextSnapshot BuildMetadataSnapshotOnly()
    {
        // 与初始快照一致：所有角色角色卡 + 实时角色上下文；未激活角色不预读。
        var storageRoot = FindStorageRoot();
        var items = new List<ContextItem>();
        var characters = GetCharacters();
        var activityMap = chatActivitySystem.GetAllChatActivities().ToDictionary(a => a.Character.Name, a => a);
        foreach (var character in characters)
        {
            var owner = character.Name;
            if (activityMap.TryGetValue(owner, out var activity))
            {
                AddLiveContextSummary(activity, owner, IsMemoryEnabled(character), items);
                AddArchivedMemorySummaries(character, owner, storageRoot, items);
                AddLocalMediaFilesForCharacter(character, storageRoot, items);
            }
        }
        if (storageRoot != null) AddGlobalSources(storageRoot, items);
        return new ContextSnapshot { Items = items };
    }

    string ReadFullContent(ContextItem item)
    {
        if (item.SourceKey == "offline-system")
        {
            var character = GetCharacterRequired(item.Owner);
            return BuildOfflineSystemPrompt(character, FindStorageRoot());
        }
        if (item.SourceKey == "offline-history")
        {
            var wanted = ExtractHistoryIndex(item.Title);
            var index = 0;
            using var reader = new JsonTextReader(new StreamReader(item.Path));
            if (reader.Read() && reader.TokenType == JsonToken.StartArray)
            {
                while (reader.Read() && reader.TokenType != JsonToken.EndArray)
                {
                    if (index == wanted)
                    {
                        var jo = JObject.Load(reader);
                        return jo.Value<string>("Content") ?? "";
                    }
                    reader.Skip();
                    index++;
                }
            }
        }
        if (item.SourceKey?.StartsWith("archive-L") == true && File.Exists(item.Path))
            return File.ReadAllText(item.Path);
        if (item.SourceKey == "live-system")
        {
            var activity = FindActivity(item.Owner);
            if (activity != null)
            {
                var idx = ExtractHistoryIndex(item.Title);
                if (idx >= 0 && idx < activity.ChatBot.ChatHistory.Count)
                    return activity.ChatBot.ChatHistory[idx].Content ?? "";
            }
        }
        if (item.SourceKey == "live-history")
        {
            var activity = FindActivity(item.Owner);
            if (activity != null)
            {
                var idx = MessageIndexFromTitle(item.Title);
                if (idx >= 0 && idx < activity.ChatBot.ChatHistory.Count)
                    return activity.ChatBot.ChatHistory[idx].Content ?? "";
            }
        }
        if (item.SourceKey == "character-prompt" && File.Exists(item.Path))
            return JObject.Parse(File.ReadAllText(item.Path)).Value<string>("Prompt") ?? "";
        // 兜底：路径型条目（notes / skills / worldbook 等）直接从文件读全文。
        if (!string.IsNullOrWhiteSpace(item.Path) && File.Exists(item.Path))
            return File.ReadAllText(item.Path);
        // 走不到任何读取路径时，如果这条正文本来就是被预算截断过的预览，
        // 绝不能把预览当成全文返回 —— 前端拿到它就会直接写回聊天记录/文件。
        if (item.Truncated)
            throw new InvalidOperationException(
                $"条目「{item.Title}」（{item.SourceKey}）的正文在状态报文里是截断预览，且没有可用的按需读取路径。" +
                "为避免把预览写回原数据，已阻止这次读取。");
        return item.Content;
    }


    void AddLocalMediaFiles(List<Character> characters, string? storageRoot, List<ContextItem> items)
    {
        if (string.IsNullOrWhiteSpace(storageRoot)) return;
        var imageExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".gif", ".webp" };
        var audioExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".mp3", ".wav", ".ogg", ".m4a" };
        foreach (var character in characters)
        {
            var dir = ResolveCharacterDirectory(character, storageRoot);
            if (dir == null) continue;
            var storageDir = Path.Combine(dir, "Storage");
            if (!Directory.Exists(storageDir)) continue;
            var files = Directory.EnumerateFiles(storageDir, "*.*", SearchOption.AllDirectories)
                .Where(f => imageExts.Contains(Path.GetExtension(f)) || audioExts.Contains(Path.GetExtension(f)))
                .Take(120);
            foreach (var file in files)
            {
                var isImage = imageExts.Contains(Path.GetExtension(file));
                var modality = isImage ? "image" : "audio";
                var exists = File.Exists(file);
                items.Add(new ContextItem
                {
                    Id = StableId(character.Name, "local-media", file),
                    Category = "本地多模态",
                    Kind = "local-file",
                    Modality = modality,
                    Owner = character.Name,
                    Title = Path.GetFileName(file),
                    Content = "",
                    Path = file,
                    Source = "角色 Storage 文件夹",
                    SourceKey = "local-media",
                    Origin = "disk",
                    ReadOnly = true,
                    Size = exists ? new FileInfo(file).Length : 0,
                    EstimatedTokens = 120,
                    Attachments = new List<ContextAttachment>
                    {
                        new()
                        {
                            Modality = modality,
                            Status = exists ? "ok" : "missing",
                            Title = Path.GetFileName(file),
                            Path = file,
                            Detail = exists ? "本地文件可访问" : "文件路径丢失",
                            Size = exists ? (int)new FileInfo(file).Length : 0
                        }
                    },
                    Tags = new List<string> { "本地文件", modality },
                    UpdatedAt = File.GetLastWriteTime(file)
                });
            }
        }
    }


    void AddLocalMediaFilesForCharacter(Character character, string? storageRoot, List<ContextItem> items)
    {
        if (string.IsNullOrWhiteSpace(storageRoot)) return;
        AddLocalMediaFiles(new List<Character> { character }, storageRoot, items);
    }

    ContextAttachment BuildMediaAttachment(string modality, Uri? uri, string? dataUri, string? mime)
    {
        // 1) Base64 / Data URI：直接可用。初始快照为了避免巨型 IPC，不携带正文。
        if (!string.IsNullOrWhiteSpace(dataUri) && dataUri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var bytes = TryBase64Length(dataUri);
            return new ContextAttachment
            {
                Modality = modality,
                Status = "embedded",
                Title = "内嵌 Base64",
                Path = dataUri,
                Detail = dataUri.Length > 90 ? dataUri[..90] + "…" : dataUri,
                Size = bytes
            };
        }

        var hasEmbeddedSource = uri != null && uri.OriginalString.StartsWith("data:", StringComparison.OrdinalIgnoreCase);
        if (hasEmbeddedSource)
        {
            var data = uri!.OriginalString;
            var bytes = TryBase64Length(data);
            return new ContextAttachment
            {
                Modality = modality,
                Status = "embedded-lazy",
                Title = "内嵌 Base64（点击加载）",
                Path = "",
                Detail = data.Length > 90 ? data[..90] + "…" : data,
                Size = bytes
            };
        }

        // 2) 路径 / URI
        var path = uri?.IsFile == true ? uri.LocalPath : uri?.ToString();
        if (!string.IsNullOrWhiteSpace(path))
        {
            var exists = !uri!.IsFile || File.Exists(path);
            if (exists)
            {
                var size = uri.IsFile && File.Exists(path) ? (int)new FileInfo(path).Length : 0;
                return new ContextAttachment { Modality = modality, Status = "ok", Title = Path.GetFileName(path), Path = path, Size = size };
            }
            return new ContextAttachment
            {
                Modality = modality,
                Status = "missing",
                Title = "文件路径丢失",
                Path = path,
                Detail = "路径不存在，无法加载该文件"
            };
        }

        return new ContextAttachment { Modality = modality, Status = "missing", Title = "无可用来源", Detail = "既没有文件路径，也没有 base64 数据" };
    }

    static int TryBase64Length(string dataUri)
    {
        try
        {
            var comma = dataUri.IndexOf(',');
            if (comma < 0) return 0;
            var b64 = dataUri[(comma + 1)..];
            return b64.Length * 3 / 4;
        }
        catch { return 0; }
    }

    void AddEditableCharacterPrompt(Character character, string owner, string? storageRoot, List<ContextItem> items)
    {
        var dir = ResolveCharacterDirectory(character, storageRoot);
        var indexPath = dir == null ? "" : Path.Combine(dir, "index.json");
        items.Add(new ContextItem
        {
            Category = "角色预设",
            Kind = "character-prompt",
            Modality = "text",
            Owner = owner,
            Title = "角色原始设定 Prompt（可编辑）",
            Content = character.Prompt ?? "",
            Path = indexPath,
            Source = "Character/index.json#Prompt",
            SourceKey = "character-prompt",
            Origin = "disk",
            ReadOnly = false,
            EstimatedTokens = EstimateTokens(character.Prompt ?? ""),
            Id = StableId(owner, "character-prompt", indexPath),
            Tags = new List<string> { "持久化字段", "编辑后写入index.json" },
            UpdatedAt = indexPath != "" && File.Exists(indexPath) ? File.GetLastWriteTime(indexPath) : null
        });
    }

    string BuildOfflineSystemPrompt(Character character, string? storageRoot)
    {
        var storageKey = string.IsNullOrWhiteSpace(character.StorageKey)
            ? Path.Combine("Character", character.Name)
            : character.StorageKey;
        var privateFolder = Path.Combine(storageRoot ?? "", storageKey, "Storage");
        return $"""
                         这是你的人物信息：
                         - 名称：{character.Name}
                         - 生日：{character.Birthday}
                         - 简介：{character.Description}
                         - 设定：
                         {character.Prompt}

                         这是你的私人文件夹：
                         {privateFolder}
                         """;
    }

    void AddOfflinePrompt(Character character, string owner, string? storageRoot, List<ContextItem> items)
    {
        var dir = ResolveCharacterDirectory(character, storageRoot);
        var indexPath = dir == null ? "" : Path.Combine(dir, "index.json");
        var fullPrompt = BuildOfflineSystemPrompt(character, storageRoot);
        items.Add(new ContextItem
        {
            Id = StableId(owner, "offline-system", indexPath),
            Category = "角色预设",
            Kind = "system",
            Modality = "text",
            Owner = owner,
            Title = "角色系统提示词（离线重建）",
            Content = Limit(fullPrompt),
            Path = indexPath,
            Source = "Character/index.json",
            SourceKey = "offline-system",
            Origin = "disk",
            ReadOnly = true,
            EstimatedTokens = EstimateTokens(fullPrompt),
            Tags = new List<string> { "角色未激活", "离线重建", "系统装配快照只读" },
            UpdatedAt = indexPath != "" && File.Exists(indexPath) ? File.GetLastWriteTime(indexPath) : null
        });
    }

    void AddOfflineHistory(Character character, string owner, string? characterDir, List<ContextItem> items,
        int maxEntries = int.MaxValue, int previewChars = 0)
    {
        if (string.IsNullOrWhiteSpace(characterDir)) return;
        var historyPath = Path.Combine(characterDir, "Memory", "History.json");
        if (!File.Exists(historyPath)) return;

        try
        {
            var index = 0;
            using var reader = new JsonTextReader(new StreamReader(historyPath));
            if (!reader.Read() || reader.TokenType != JsonToken.StartArray) return;

            while (index < maxEntries && reader.Read() && reader.TokenType != JsonToken.EndArray)
            {
                var jo = JObject.Load(reader);
                var role = jo["Role"]?.Value<string>("Label") ?? "unknown";
                var text = jo.Value<string>("Content") ?? "";
                var level = jo["MemoryMeta"]?.Value<int?>("Level") ?? 0;
                var startTime = jo["MemoryMeta"]?.Value<DateTime?>("StartTime");

                var category = level > 0 ? "长期记忆" : "历史对话";
                var isMemoryMark = TryParseMemory(text, out var parsedLevel) && parsedLevel > 0;
                if (isMemoryMark) category = "长期记忆";

                items.Add(new ContextItem
                {
                    Category = category,
                    Kind = role,
                    Modality = DetectTextModality(text),
                    Owner = owner,
                    Title = $"{role} #{index}",
                    // 初始快照只放短预览，防止超大 History 重建拖垮主电路；双击条目按需读取全文。
                    Content = PreviewLimit(text, previewChars),
                    Path = historyPath,
                    Source = "Memory/History.json",
                    SourceKey = "offline-history",
                    Origin = "disk",
                    ReadOnly = false,
                    EstimatedTokens = EstimateTokens(text),
                    MemoryLevel = isMemoryMark ? parsedLevel : level,
                    Tags = new List<string>
                    {
                        role, "离线持久化", "当前有界窗口",
                        "不含多模态Items"
                    },
                    Id = StableId(owner, "history", historyPath, index.ToString()),
                    UpdatedAt = startTime ?? File.GetLastWriteTime(historyPath)
                });
                index++;
            }

            if (index >= maxEntries)
            {
                items.Add(new ContextItem
                {
                    Owner = owner,
                    Title = "历史索引已截断（性能保护）",
                    Category = "历史对话",
                    Content = "初始快照只索引前 " + maxEntries + " 条短预览以避免主界面断线；请使用“聊天上下文 → 历史搜索/分页”查看全部与全文。",
                    Source = "Memory/History.json",
                    SourceKey = "offline-history-truncated",
                    Origin = "disk",
                    ReadOnly = true,
                    Tags = new List<string> { "分页查看" }
                });
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "读取离线 History.json 失败 {Path}", historyPath);
        }
    }

    void AddArchivedMemory(Character character, string owner, string? storageRoot, List<ContextItem> items)
    {
        var dir = ResolveCharacterDirectory(character, storageRoot);
        if (dir == null) return;
        var memoryRoot = Path.Combine(dir, "Memory");
        if (!Directory.Exists(memoryRoot)) return;

        foreach (var levelDir in Directory.GetDirectories(memoryRoot).OrderBy(d => d))
        {
            var levelText = Path.GetFileName(levelDir);
            if (!int.TryParse(levelText.TrimStart('L'), out var level)) continue;
            foreach (var file in Directory.GetFiles(levelDir, "*.txt").OrderBy(f => f))
            {
                var content = File.ReadAllText(file);
                items.Add(new ContextItem
                {
                    Category = "归档记忆文件",
                    Kind = "archive",
                    Modality = "text",
                    Owner = owner,
                    Title = Path.GetFileName(file),
                    Content = Limit(content),
                    Path = file,
                    Source = $"Memory/{levelText}",
                    SourceKey = $"archive-L{level}",
                    Origin = "disk",
                    ReadOnly = false,
                    Size = new FileInfo(file).Length,
                    EstimatedTokens = EstimateTokens(content),
                    MemoryLevel = level,
                    Tags = new List<string> { levelText, "MemoryService归档", "可编辑文本文件" },
                    Id = StableId(owner, "archive", file),
                    UpdatedAt = File.GetLastWriteTime(file)
                });
            }
        }
    }

    void AddGlobalSources(string storageRoot, List<ContextItem> items)
    {
        if (storageRoot == null) return;

        var configRoot = Path.Combine(storageRoot, "Configuration");
        foreach (var file in Directory.Exists(configRoot) ? Directory.GetFiles(configRoot, "*.json") : Array.Empty<string>())
        {
            var content = File.ReadAllText(file);
            items.Add(new ContextItem
            {
                Category = "配置预设", Kind = "global-configuration", Modality = "json",
                Owner = "全局", Title = Path.GetFileName(file), Content = Limit(content),
                Path = file, Source = "Storage/Configuration", SourceKey = "global-config",
                Origin = "disk", Size = new FileInfo(file).Length,
                EstimatedTokens = EstimateTokens(content),
                Tags = new List<string> { "全局配置" }, UpdatedAt = File.GetLastWriteTime(file)
            });
        }
    }

    void BuildSummaries(ContextSnapshot snapshot, Dictionary<string, ChatActivity> activities)
    {
        snapshot.Owners = snapshot.Items.GroupBy(i => i.Owner).Select(g =>
        {
            var character = characterSystem.GetAllCharacters().FirstOrDefault(c => c.Name == g.Key);
            return new OwnerSummary
            {
                Name = g.Key,
                Count = g.Count(),
                Tokens = g.Sum(i => i.EstimatedTokens),
                Active = activities.ContainsKey(g.Key),
                MemoryEnabled = character != null && IsMemoryEnabled(character)
            };
        }).OrderByDescending(o => o.Tokens).ToList();

        snapshot.Categories = snapshot.Items.GroupBy(i => i.Category).Select(g => new CategorySummary
        {
            Name = g.Key, Count = g.Count(), Tokens = g.Sum(i => i.EstimatedTokens)
        }).OrderByDescending(c => c.Tokens).ToList();

        snapshot.Modalities = snapshot.Items.GroupBy(i => i.Modality).Select(g => new ModalitySummary
        {
            Name = g.Key, Count = g.Count(), Tokens = g.Sum(i => i.EstimatedTokens)
        }).OrderByDescending(m => m.Count).ToList();

        snapshot.MemoryLevels = snapshot.Items
            .Where(i => i.MemoryLevel > 0)
            .GroupBy(i => i.MemoryLevel)
            .Select(g => new MemoryLevelInfo { Level = g.Key, Count = g.Count(), Tokens = g.Sum(i => i.EstimatedTokens) })
            .OrderBy(i => i.Level).ToList();
    }



    void FillLiveAttachments(ContextItem item)
    {
        if (item.SourceKey != "live-history" && item.SourceKey != "live-system") return;
        var activity = FindActivity(item.Owner);
        if (activity == null) return;
        var idx = item.SourceKey == "live-system" ? 0 : MessageIndexFromTitle(item.Title);
        if (idx < 0 || idx >= activity.ChatBot.ChatHistory.Count) return;
        var message = activity.ChatBot.ChatHistory[idx];
        item.Attachments = ExtractAttachments(message, includeBinary: true);
        item.EstimatedTokens = EstimateTokens(message.Content ?? "") + item.Attachments.Sum(a => 120);
    }

    int MessageIndexFromTitle(string title)
    {
        var match = Regex.Match(title ?? "", @"#(\d+)$");
        return match.Success ? int.Parse(match.Groups[1].Value) : -1;
    }

    /// <summary>世界书条目的单条报文预算（转义后字节）。与 state / plan 共用同一套口径。</summary>
    const int WorldBookBudgetBytes = ContextStateBudget.DefaultBudgetBytes;

    /// <summary>
    /// 读角色目录下的 WorldBook.json，规范化成条目行。
    /// id 用「源文件里的 id，没有就按位置生成 wb&lt;i&gt;」—— 必须**稳定**，
    /// 因为面板的「读取全文」是按 id 回查的（见 TavernImport.NormalizeEntries 的 fallbackId）。
    /// </summary>
    List<JObject> ReadWorldBookRows(string owner)
    {
        var character = GetCharacters().FirstOrDefault(c => c.Name == owner);
        var dir = character == null ? null : ResolveCharacterDirectory(character, FindStorageRoot());
        var file = dir == null ? "" : Path.Combine(dir, "WorldBook.json");
        if (string.IsNullOrWhiteSpace(file) || !File.Exists(file)) return new List<JObject>();
        // 用 TavernImport.NormalizeEntries 读：WorldBook.json 可能来自本插件
        // （Entries/Title/Keywords 大写），也可能来自酒馆（entries/comment/keys 小写）。
        // 以前只认 jo["Entries"]，导入酒馆世界书后「面板里一条都没有」就是这么来的。
        return TavernImport.NormalizeEntries(JObject.Parse(File.ReadAllText(file)), i => "wb" + i).OfType<JObject>().ToList();
    }

    /// <summary>
    /// 按预算打包世界书条目并下发。
    ///
    /// <para><paramref name="fullText"/> = false（首次打开面板）：**每条都要出现**。
    /// 正文放得下给全文，放不下给预览并打 <c>truncated</c> —— 一张 40 条长条目的卡，
    /// 世界书正文就有 7.8 万字，序列化后 800+ KB，超过单条 IPC 报文的硬上限，
    /// 旧逻辑会被桥直接拦掉，用户看到的是「面板一条都没有」。现在至少结构完整可见。</para>
    ///
    /// <para><paramref name="fullText"/> = true（<c>worldbook:page</c>，按需读全文）：
    /// 装到预算满就停，回传 <c>nextOffset</c> 让前端翻页；每页都是**完整正文**。</para>
    /// </summary>
    void SendWorldBook(string owner, IReadOnlyList<JObject> rows, int offset, bool fullText)
    {
        // 打包规则在 ContextStateBudget.PackWorldBookRows：纯函数、可单元测试，
        // 且与 state / plan 共用同一套「放得下给全文、放不下给预览」的口径。
        var (list, nextOffset, truncatedCount) = ContextStateBudget.PackWorldBookRows(rows, offset, fullText, WorldBookBudgetBytes);
        SendWindow("worldbook-state", new
        {
            owner,
            entries = list,
            offset,
            // nextOffset 直接由「这次服务了哪一段」推出，前端下次原样传回来即可。
            nextOffset,
            remaining = Math.Max(0, rows.Count - nextOffset),
            truncatedCount,
            total = rows.Count
        });
    }

    void GetWorldBook(string owner)
    {
        _ = Task.Run(() =>
        {
            try { SendWorldBook(owner, ReadWorldBookRows(owner), 0, false); }
            catch (Exception ex) { SendWindow("error", new { message = "读取世界书失败：" + ex.Message }); }
        });
    }

    /// <summary>按需读取世界书正文：一次尽量多装几条**完整**条目，前端靠 nextOffset 继续翻。</summary>
    void GetWorldBookPage(string owner, JsonElement payload)
    {
        _ = Task.Run(() =>
        {
            try
            {
                var offset = payload.TryGetProperty("offset", out var offEl) && offEl.TryGetInt32(out var off) ? Math.Max(0, off) : 0;
                SendWorldBook(owner, ReadWorldBookRows(owner), offset, true);
            }
            catch (Exception ex) { SendWindow("error", new { message = "读取世界书失败：" + ex.Message }); }
        });
    }

    /// <summary>取单条世界书的完整正文（面板在报文被截断后按需读取）。</summary>
    void GetWorldBookEntry(string owner, JsonElement payload)
    {
        var id = JsonString(payload, "id") ?? "";
        _ = Task.Run(() =>
        {
            try
            {
                var row = ReadWorldBookRows(owner).FirstOrDefault(r => string.Equals(r.Value<string>("Id"), id, StringComparison.Ordinal));
                if (row == null)
                {
                    SendWindow("worldbook-entry", new { owner, id, error = "找不到这条世界书条目（可能已被删除或改动过）：" + id });
                    return;
                }
                var content = row.Value<string>("Content") ?? "";
                SendWindow("worldbook-entry", new { owner, id, title = row.Value<string>("Title") ?? "", content, contentLength = content.Length });
            }
            catch (Exception ex) { SendWindow("worldbook-entry", new { owner, id, error = ex.Message }); }
        });
    }

    /// <summary>
    /// 世界书条目的「酒馆别名」。TavernImport 读取时酒馆键**优先**
    /// （例如 Title 取 <c>comment ?? name ?? Title</c>，Enabled 还要再 <c>&amp;&amp; !disable</c>）。
    /// 所以在原地修改原始条目之前，必须把这些别名删掉，否则面板里改的标题 / 启用状态
    /// 会被残留的 comment / disable 覆盖回去 —— 表现就是「改了但没生效」。
    /// </summary>
    static readonly string[] WorldBookShadowKeys =
    {
        "id", "comment", "name", "keys", "key", "content",
        "enabled", "disable", "constant", "insertion_order", "order"
    };
    static void StripShadowKeys(JObject row)
    {
        foreach (var key in WorldBookShadowKeys) row.Remove(key);
    }
    static string WorldBookRowKey(string? title, string? content)
        => (title ?? "").Trim() + "\u0000" + (content ?? "").Trim();
    static string WorldBookRowKey(JObject row)
        => WorldBookRowKey(
            row.Value<string>("Title") ?? row.Value<string>("comment") ?? row.Value<string>("name"),
            row.Value<string>("Content") ?? row.Value<string>("content"));

    void SaveWorldBook(string owner, JsonElement payload)
    {
        _ = Task.Run(() =>
        {
            try
            {
                var character = GetCharacterRequired(owner);
                var dir = ResolveCharacterDirectory(character, FindStorageRoot()) ?? throw new InvalidOperationException("角色目录不存在");
                var file = Path.Combine(dir, "WorldBook.json");

                // 读回原始条目。世界书面板只编辑 7 个字段（标题/关键词/正文/启用/常驻/顺序/ID），
                // 如果按面板字段把整份文件重建一遍，用户从酒馆导入的世界书会在第一次「保存」之后
                // 丢掉 position / depth / probability / secondary_keys / extensions 等酒馆专属字段。
                // 这里的做法是：面板字段照常覆盖，**其余字段原样带过来**。
                var raw = new List<JObject>();
                if (File.Exists(file))
                {
                    try { raw = TavernImport.EntryRows(JObject.Parse(File.ReadAllText(file))).Select(e => (JObject)e.DeepClone()).ToList(); }
                    catch (Exception parseEx) { ContextTrace.Write("worldbook save: existing parse failed: " + parseEx.Message); }
                }
                // 索引必须用**面板看到的那套 id**：面板是从
                // TavernImport.NormalizeEntries(..., i => "wb" + i) 拿的 id，
                // 而这里 raw 是原始行（可能是小写 id、也可能完全没有 id）。
                // 两边不一致的话，「读取全文」回来的 id 在保存时找不到原始行，
                // 酒馆专属字段（position/depth/secondary_keys…）就会被丢掉。
                var idArray = new JArray();
                foreach (var row in raw) idArray.Add(row);
                var normalizedIds = TavernImport.NormalizeEntries(idArray, i => "wb" + i);
                var byId = new Dictionary<string, JObject>(StringComparer.Ordinal);
                var byKey = new Dictionary<string, JObject>(StringComparer.Ordinal);
                for (var i = 0; i < raw.Count; i++)
                {
                    var normalized = i < normalizedIds.Count ? normalizedIds[i] as JObject : null;
                    if (normalized == null) continue;
                    var id = normalized.Value<string>("Id") ?? "";
                    if (id.Length > 0) byId[id] = raw[i];
                    var key = WorldBookRowKey(normalized);
                    if (key.Length > 0) byKey[key] = raw[i];
                }

                var entries = new JArray();
                if (payload.TryGetProperty("entries", out var list) && list.ValueKind == JsonValueKind.Array)
                {
                    foreach (var entry in list.EnumerateArray())
                    {
                        var keywords = new JArray();
                        if (entry.TryGetProperty("keywords", out var kw) && kw.ValueKind == JsonValueKind.Array)
                            foreach (var k in kw.EnumerateArray()) keywords.Add(k.GetString());
                        var id = entry.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
                        var title = entry.TryGetProperty("title", out var titleEl) ? titleEl.GetString() ?? "" : "";
                        var content = entry.TryGetProperty("content", out var contentEl) ? contentEl.GetString() ?? "" : "";
                        // 正文没随报文下发的条目（面板显示的是预览）：**保留磁盘上的原文**。
                        // 不然一次「保存世界书」就会把预览写回文件，正文永久丢失。
                        var truncated = entry.TryGetProperty("truncated", out var trEl) && trEl.ValueKind == JsonValueKind.True;
                        // 先按 ID 找原始条目，找不到再按「标题 + 正文」找。
                        JObject? origin = null;
                        if (id.Length > 0) byId.TryGetValue(id, out origin);
                        if (origin == null) byKey.TryGetValue(WorldBookRowKey(title, content), out origin);
                        // 原地改原始行：这样 position / depth / probability / secondary_keys /
                        // extensions 等面板管不到的字段会原样留下。改之前先删掉「优先级更高」的
                        // 酒馆别名（见 StripShadowKeys），否则标题/启用状态会被残留键覆盖回去。
                        var row = origin ?? new JObject();
                        if (origin != null) StripShadowKeys(row);
                        row["Id"] = id.Length > 0 ? id : Guid.NewGuid().ToString("N");
                        row["Title"] = title;
                        row["Keywords"] = keywords;
                        if (truncated && origin != null) ContextTrace.Write($"worldbook save: keep original content id={id} (truncated preview not written back)");
                        else row["Content"] = content;
                        row["Enabled"] = !(entry.TryGetProperty("enabled", out var enabled) && enabled.ValueKind == JsonValueKind.False);
                        row["Constant"] = entry.TryGetProperty("constant", out var constant) && constant.ValueKind == JsonValueKind.True;
                        row["InsertionOrder"] = entry.TryGetProperty("insertionOrder", out var order) && order.TryGetInt32(out var orderValue) ? orderValue : 100;
                        row["UpdatedAt"] = DateTime.Now;
                        entries.Add(row);
                    }
                }
                var book = new JObject { ["Entries"] = entries, ["UpdatedAt"] = DateTime.Now };
                if (File.Exists(file)) File.Copy(file, file + ".backup-" + DateTime.Now.ToString("yyyyMMddHHmmss"));
                AtomicWrite(file, book.ToString(Formatting.Indented));
                // 回包走和 worldbook:get 同一条预算路径 —— 以前这里直接把全部条目全文推回去，
                // 一张 40 条长条目的卡就是 800+ KB，会被桥的硬上限直接拦掉。
                SendWorldBook(owner, ReadWorldBookRows(owner), 0, false);
                QueueInitialState();
            }
            catch (Exception ex) { SendWindow("error", new { message = "保存世界书失败：" + ex.Message }); }
        });
    }

    /// <summary>把导入选项推给酒馆兼容页（目前只有「卡内世界书」这一项）。</summary>
    void SendImportOptions(string owner) => SendWindow("import-options", new { owner, cardWorldbook = CardWorldbookMode() });

    /// <summary>酒馆兼容页修改卡内世界书策略：ask（每次弹窗询问）/ always / never。</summary>
    void SaveImportOptionsFromUi(string owner, JsonElement payload)
    {
        var mode = JsonString(payload, "cardWorldbook") ?? "ask";
        if (mode is not ("ask" or "always" or "never")) mode = "ask";
        SaveImportOption("cardWorldbook", mode);
        SendWindow("import-options", new { owner, cardWorldbook = CardWorldbookMode() });
    }

    void ImportTavernCard()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var parent = windowService?.Window ?? Electron.WindowManager.BrowserWindows.FirstOrDefault()
                    ?? throw new InvalidOperationException("没有可用的插件窗口");
                var options = new OpenDialogOptions
                {
                    Title = "选择酒馆角色卡（.json 或 .png）",
                    ButtonLabel = "导入",
                    // 酒馆社区的角色卡绝大多数是 PNG（设定藏在 tEXt 块里）。只给 json 过滤器
                    // 会让用户在自己的卡目录里根本选不到文件。
                    Filters = new[] { new FileFilter { Name = "角色卡", Extensions = new[] { "json", "png" } } },
                    Properties = new[] { ElectronNET.API.Entities.OpenDialogProperty.openFile }
                };
                var files = await Electron.Dialog.ShowOpenDialogAsync(parent, options);
                var file = files?.FirstOrDefault();
                if (string.IsNullOrWhiteSpace(file)) return;
                var root = TavernImport.ReadCard(file);
                var data = root["data"] as JObject ?? root;
                var name = UniqueName(data.Value<string>("name") ?? Path.GetFileNameWithoutExtension(file));
                // 卡内世界书：和装配页的「角色卡」导入走同一套策略与记忆（ImportOptions.cardWorldbook）。
                // 以前这里是无条件并入 —— 用户根本没有拒绝的机会。
                var cardBook = data["character_book"];
                var cardBookCount = TavernImport.EntryRows(cardBook).Count();
                var withCardWorldbook = cardBookCount > 0
                    && await AskCardWorldbookAsync(data.Value<string>("name") ?? name, cardBook, cardBookCount);
                var standard = TavernImport.CardFields.Take(TavernImport.StandardFieldCount).Select(f => data.Value<string>(f.Key)).ToArray();
                var prompt = JoinParts(standard);
                // 「特殊卡」兜底：实测有些卡 6 个标准提示词字段全空，整段设定都放在开场白里
                // （例如某张卡.png 的 first_mes 有 3 万字）。这时不要留一个空设定，
                // 退回用开场白 / 作者注释凑出「基本信息」——用户要的就是这个。
                if (string.IsNullOrWhiteSpace(prompt))
                    prompt = JoinParts(TavernImport.CardFields.Skip(TavernImport.StandardFieldCount).Select(f => data.Value<string>(f.Key)).ToArray());
                var jo = new JObject
                {
                    ["Name"] = name,
                    ["Birthday"] = DateTime.Now,
                    ["Description"] = data.Value<string>("description") ?? "",
                    ["Prompt"] = prompt,
                    ["Modules"] = new JArray(),
                    ["AutoActivate"] = false
                };
                var storageRoot = FindStorageRoot() ?? throw new InvalidOperationException("找不到 Storage 根目录");
                var dir = Path.Combine(storageRoot, "Character", name);
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "index.json"), jo.ToString());
                // 同时落一份原始酒馆卡：装配页的「角色卡」导入，以及 {{description}}/{{personality}} 等宏，
                // 读的都是角色目录下的 TavernCard.json。之前这里不写，导致从本入口导入的角色
                // 在装配页点“角色卡”会报“没有导入的酒馆卡”，而预览里却能通过官方提示词看到卡内容。
                File.WriteAllText(Path.Combine(dir, "TavernCard.json"), data.ToString());
                // 世界书：先并入卡内 character_book，再把开场白作为一条带关键词的条目补上。
                // 原来这里直接覆盖写「只有开场白」的一份，会把卡内世界书整份丢掉。
                var bookPath = Path.Combine(dir, "WorldBook.json");
                JObject? existingBook = null;
                if (File.Exists(bookPath))
                {
                    try { existingBook = JObject.Parse(File.ReadAllText(bookPath)); }
                    catch (Exception parseEx) { ContextTrace.Write("tavern card existing worldbook parse failed: " + parseEx.Message); }
                }
                // 用户选「只要角色卡」时传 null：MergeWorldBook 会原样返回已有条目，一条都不加。
                var (bookAdded, _, book) = TavernImport.MergeWorldBook(existingBook, withCardWorldbook ? cardBook : null);
                // 开场白：first_mes 与 alternate_greetings 全部落成条目。
                // 以前只写 first_mes，作者另写的备选开场在导入时就没了。
                var entries = book["Entries"] as JArray ?? new JArray();
                var greetingIndex = 0;
                foreach (var (label, text) in TavernImport.Greetings(data))
                {
                    greetingIndex++;
                    if (entries.OfType<JObject>().Any(e => string.Equals(e.Value<string>("Title"), label, StringComparison.Ordinal))) continue;
                    entries.Add(new JObject
                    {
                        ["Id"] = Guid.NewGuid().ToString("N"),
                        ["Title"] = label,
                        ["Keywords"] = greetingIndex == 1
                            ? new JArray("first_mes", "开场白")
                            : new JArray("alternate_greetings", label),
                        ["Content"] = text,
                        ["Enabled"] = true,
                        ["Constant"] = false,
                        ["InsertionOrder"] = 10 + greetingIndex - 1,
                        ["UpdatedAt"] = DateTime.Now
                    });
                }
                if (greetingIndex > 0) book["Entries"] = entries;
                var greetingNote = greetingIndex > 1 ? $"这张卡有 {greetingIndex} 条开场白，已全部写入世界书（开场白 / 开场白 2 …）。" : "";
                File.WriteAllText(bookPath, book.ToString());
                var bookNote = cardBookCount == 0
                    ? "这张卡没有内嵌世界书。"
                    : withCardWorldbook
                        ? $"已并入卡内世界书 {bookAdded} 条。"
                        : $"卡内世界书 {cardBookCount} 条按你的选择跳过（可在酒馆兼容页改回「每次询问」）。";
                if (greetingNote.Length > 0) bookNote += "\n" + greetingNote;
                SendWindow("tavern-imported", new { name, note = bookNote });
            }
            catch (Exception ex) { SendWindow("error", new { message = "导入酒馆角色卡失败：" + ex.Message }); }
        });
    }

    string UniqueName(string baseName)
    {
        baseName = string.IsNullOrWhiteSpace(baseName) ? "New Character" : Path.GetInvalidFileNameChars().Aggregate(baseName.Trim(), (a,c)=>a.Replace(c,'_'));
        var existing = new HashSet<string>(GetCharacters().Select(c=>c.Name), StringComparer.OrdinalIgnoreCase);
        if (!existing.Contains(baseName)) return baseName;
        for (var i = 2; i < 1000; i++)
        {
            var candidate = $"{baseName}-{i}";
            if (!existing.Contains(candidate)) return candidate;
        }
        return baseName + "-" + Guid.NewGuid().ToString("N").Substring(0,6);
    }

    static string JoinParts(params string?[] parts)
        => string.Join("\n\n", parts.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!.Trim()));

    void CreateCharacterFromUi(string name)
    {
        _ = Task.Run(() =>
        {
            try
            {
                var final = UniqueName(name);
                var root = FindStorageRoot() ?? throw new InvalidOperationException("找不到 Storage 根目录");
                var dir = Path.Combine(root, "Character", final);
                Directory.CreateDirectory(dir);
                var jo = new JObject
                {
                    ["Name"] = final,
                    ["Birthday"] = DateTime.Now,
                    ["Description"] = "",
                    ["Prompt"] = "",
                    ["Modules"] = new JArray(),
                    ["AutoActivate"] = false
                };
                File.WriteAllText(Path.Combine(dir, "index.json"), jo.ToString());
                SendWindow("tavern-imported", new { name = final });
            }
            catch (Exception ex) { SendWindow("error", new { message = "创建角色失败：" + ex.Message }); }
        });
    }


    void ActivateCharacter(string owner)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var character = GetCharacterRequired(owner);
                SendWindow("busy", new { message = $"正在激活 {owner}…" });
                // 分点日志：激活是多角色场景下最容易出问题的一步（现场就是激活第二个
                // 角色后整程序卡死）。留下进入/返回时间，才能判断卡在框架还是卡在 IPC。
                ContextTrace.Write($"activate begin owner={owner} activeBefore={chatActivitySystem.GetAllChatActivities().Count()}");
                await chatActivitySystem.Activate(character);
                ContextTrace.Write($"activate done owner={owner} activeAfter={chatActivitySystem.GetAllChatActivities().Count()}");
                QueueInitialState();
            }
            catch (Exception ex)
            {
                ContextTrace.Write($"activate FAILED owner={owner}: {ex.GetType().Name}: {ex.Message}");
                SendWindow("error", new { message = "激活角色失败：" + ex.Message });
            }
        });
    }

    void DeactivateCharacter(string owner)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var character = GetCharacterRequired(owner);
                SendWindow("busy", new { message = $"正在停用 {owner}…" });
                ContextTrace.Write($"deactivate begin owner={owner}");
                await chatActivitySystem.Deactivate(character);
                ContextTrace.Write($"deactivate done owner={owner}");
                QueueInitialState();
            }
            catch (Exception ex)
            {
                ContextTrace.Write($"deactivate FAILED owner={owner}: {ex.GetType().Name}: {ex.Message}");
                SendWindow("error", new { message = "停用角色失败：" + ex.Message });
            }
        });
    }

    void SendChat(string owner, string content)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                if (string.IsNullOrWhiteSpace(content)) throw new InvalidOperationException("发送内容不能为空");
                var character = GetCharacterRequired(owner);
                var activity = chatActivitySystem.GetChatActivity(character);
                if (activity == null)
                {
                    SendWindow("busy", new { message = $"角色未激活，正在先激活 {owner}…" });
                    activity = await chatActivitySystem.Activate(character);
                }

                SendWindow("busy", new { message = "消息已发送，正在等待 API 回复…" });
                var result = await activity.ChatBot.ChatAsync(content, true);
                if (result.Exception != null) throw result.Exception;
                SendWindow("chat-finished", new { owner });
                QueueInitialState();
            }
            catch (Exception ex) { SendWindow("error", new { message = "真实聊天失败：" + ex.Message }); }
        });
    }

    void QueryHistoryPage(string owner, JsonElement payload)
    {
        _ = Task.Run(() =>
        {
            try
            {
                if (FindActivity(owner) == null)
                {
                    SendWindow("owner-inactive", new { owner });
                    SendWindow("history-page", new { owner, items = new List<ContextItem>(), total = 0, offset = 0, count = 0, query = "" });
                    return;
                }
                var character = GetCharacterRequired(owner);
                var dir = ResolveCharacterDirectory(character, FindStorageRoot()) ?? throw new InvalidOperationException("角色目录不存在");
                var path = Path.Combine(dir, "Memory", "History.json");
                if (!File.Exists(path)) { SendWindow("history-page", new { owner, items = new List<ContextItem>(), total = 0 }); return; }

                var offset = payload.TryGetProperty("offset", out var off) && off.TryGetInt32(out var offValue) ? Math.Max(0, offValue) : 0;
                var take = payload.TryGetProperty("count", out var cnt) && cnt.TryGetInt32(out var cntValue) ? Math.Clamp(cntValue, 10, 100) : 50;
                var query = payload.TryGetProperty("query", out var q) ? q.GetString() ?? "" : "";
                var items = new List<ContextItem>();
                var total = 0;
                var index = 0;

                using var reader = new JsonTextReader(new StreamReader(path));
                if (!reader.Read() || reader.TokenType != JsonToken.StartArray) throw new InvalidDataException("History.json 不是数组");
                while (reader.Read() && reader.TokenType != JsonToken.EndArray)
                {
                    var jo = JObject.Load(reader);
                    var text = jo.Value<string>("Content") ?? "";
                    var matches = string.IsNullOrWhiteSpace(query) || text.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
                        || (jo["Role"]?.Value<string>("Label") ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
                    if (matches)
                    {
                        total++;
                        var position = total - 1;
                        if (position >= offset && position < offset + take)
                        {
                            var role = jo["Role"]?.Value<string>("Label") ?? "unknown";
                            var isMemory = TryParseMemory(text, out var level) && level > 0;
                            items.Add(new ContextItem
                            {
                                Id = StableId(owner, "history", path, index.ToString()),
                                Category = isMemory ? "长期记忆" : "历史对话",
                                Kind = role,
                                Modality = DetectTextModality(text),
                                Owner = owner,
                                Title = $"{role} #{index}",
                                Content = ShortPreview(text),
                                Path = path,
                                Source = "Memory/History.json",
                                SourceKey = "offline-history",
                                Origin = "disk",
                                ReadOnly = false,
                                EstimatedTokens = EstimateTokens(text),
                                MemoryLevel = level,
                                Tags = new List<string> { role, "分页结果", "不含多模态Items" }
                            });
                        }
                    }
                    index++;
                }
                SendWindow("history-page", new { owner, items, total, offset, count = take, query });
            }
            catch (Exception ex) { SendWindow("error", new { message = "读取历史分页失败：" + ex.Message }); }
        });
    }


    // ===== 上下文覆盖（酒馆兼容）=====

    ContextOverrideBuilder GetOverrideBuilder()
    {
        if (overrideBuilder != null) return overrideBuilder;
        overrideBuilder = new ContextOverrideBuilder(
            owner =>
            {
                var character = GetCharacters().FirstOrDefault(c => c.Name == owner);
                var dir = character == null ? null : ResolveCharacterDirectory(character, FindStorageRoot());
                var path = dir == null ? null : Path.Combine(dir, "index.json");
                return string.IsNullOrEmpty(path) || !File.Exists(path) ? null : JObject.Parse(File.ReadAllText(path));
            },
            presetName =>
            {
                var file = Path.Combine(GetPresetDirectory(), SafeFileName(presetName) + ".json");
                return File.Exists(file) ? File.ReadAllText(file) : null;
            },
            owner =>
            {
                var character = GetCharacters().FirstOrDefault(c => c.Name == owner);
                var dir = character == null ? null : ResolveCharacterDirectory(character, FindStorageRoot());
                var path = dir == null ? null : Path.Combine(dir, "WorldBook.json");
                return string.IsNullOrEmpty(path) || !File.Exists(path) ? null : JObject.Parse(File.ReadAllText(path));
            });
        return overrideBuilder;
    }

    // 临时覆盖按“角色”保存：OverrideState/角色名/context.txt（不写系统角色 index.json）
    string GetOverrideDirectory(string owner)
    {
        // 目录创建失败不能让“应用/还原”整个操作崩掉：这里退到临时目录。
        try
        {
            var root = FindStorageRoot();
            var baseDir = string.IsNullOrWhiteSpace(root)
                ? Path.Combine(pluginSystem.PluginContext.GetPluginDirectoryPath("Marisa.ContextManager"), "OverrideState")
                : Path.Combine(root, "ContextManager", "OverrideState");
            var dir = Path.Combine(baseDir, SafeFileName(owner));
            Directory.CreateDirectory(dir);
            return dir;
        }
        catch (Exception ex)
        {
            ContextTrace.Write("override directory failed: " + ex.Message);
            return Path.Combine(Path.GetTempPath(), "Marisa.ContextManager", SafeFileName(owner));
        }
    }

    string GetOverrideFile(string owner) => Path.Combine(GetOverrideDirectory(owner), "context.txt");

    string? ReadTempOverride(string owner)
    {
        try
        {
            var file = GetOverrideFile(owner);
            return File.Exists(file) ? File.ReadAllText(file) : null;
        }
        catch (Exception ex) { ContextTrace.Write("read temp override failed: " + ex.Message); return null; }
    }

    void WriteTempOverride(string owner, string content)
    {
        try { File.WriteAllText(GetOverrideFile(owner), content); }
        catch (Exception ex) { ContextTrace.Write("write temp override failed: " + ex.Message); }
    }

    void DeleteTempOverride(string owner)
    {
        try
        {
            var file = GetOverrideFile(owner);
            if (File.Exists(file)) File.Delete(file);
        }
        catch (Exception ex) { ContextTrace.Write("delete temp override failed: " + ex.Message); }
    }

    void ApplyOverride(string owner, JsonElement payload)
    {
        _ = Task.Run(() =>
        {
            lock (stateLock)
            {
                try
                {
                    var character = GetCharacterRequired(owner);

                    // 以前端传来的选项为准，同步到配置
                    var pmode = JsonString(payload, "mode");
                    if (!string.IsNullOrWhiteSpace(pmode)) Config.OverrideMode = pmode;
                    var ppreset = JsonString(payload, "activePreset");
                    if (ppreset != null) Config.ActivePreset = ppreset;
                    if (payload.TryGetProperty("useCharacterCard", out var uc) && (uc.ValueKind == JsonValueKind.True || uc.ValueKind == JsonValueKind.False)) Config.UseCharacterCard = uc.GetBoolean();
                    if (payload.TryGetProperty("useWorldBook", out var uw) && (uw.ValueKind == JsonValueKind.True || uw.ValueKind == JsonValueKind.False)) Config.UseWorldBook = uw.GetBoolean();
                    if (payload.TryGetProperty("applyMacros", out var um) && (um.ValueKind == JsonValueKind.True || um.ValueKind == JsonValueKind.False)) Config.ApplyMacros = um.GetBoolean();

                    var mode = (Config.OverrideMode ?? "Off");
                    var rendered = GetOverrideBuilder().Build(owner, Config);

                    if (mode == "Permanent")
                    {
                        // 完全覆盖：写入 index.json 的 Prompt（永久）。删除临时文件。
                        UpdateIndexJson(owner, jo => jo["Prompt"] = rendered);
                        DeleteTempOverride(owner);
                    }
                    else if (mode == "Temporary")
                    {
                        // 临时覆盖：不改 index.json，把渲染内容存插件目录并注入运行态
                        WriteTempOverride(owner, rendered);
                        var activity = chatActivitySystem.GetChatActivity(character);
                        if (activity != null)
                        {
                            activity.ChatBot.EditChatHistory(thread =>
                            {
                                if (thread.ChatHistory.Count > 0)
                                    thread.ChatHistory[0].Content = rendered;
                            }, "ContextManager 临时覆盖注入");
                        }
                    }
                    else
                    {
                        throw new InvalidOperationException("当前覆盖模式为关闭");
                    }

                    SendOverrideState(owner);
                    QueueInitialState();
                }
                catch (Exception ex) { SendWindow("error", new { message = "应用覆盖失败：" + ex.Message }); }
            }
        });
    }

    void ClearOverride(string owner)
    {
        _ = Task.Run(() =>
        {
            lock (stateLock)
            {
                try
                {
                    var character = GetCharacterRequired(owner);
                    DeleteTempOverride(owner);

                    // 若 index.json 的 Prompt 含有注入块（完全覆盖后想还原），剥离后还原为原始角色设定
                    var dir = ResolveCharacterDirectory(character, FindStorageRoot()) ?? throw new InvalidOperationException("角色目录不存在");
                    var indexPath = Path.Combine(dir, "index.json");
                    var jo = JObject.Parse(File.ReadAllText(indexPath));
                    var prompt = jo.Value<string>("Prompt") ?? "";
                    if (prompt.Contains(ContextOverrideBuilder.BlockOpenPrefix))
                        jo["Prompt"] = ContextOverrideBuilder.StripOverrideBlocks(prompt);
                    File.WriteAllText(indexPath, jo.ToString(Formatting.Indented));

                    // 运行态用 index.json 重建 index[0]
                    var activity = chatActivitySystem.GetChatActivity(character);
                    if (activity != null)
                    {
                        var fresh = JObject.Parse(File.ReadAllText(indexPath));
                        var rebuilt = BuildOfficialSystemPrompt(fresh.ToObject<Character>() ?? character, FindStorageRoot());
                        activity.ChatBot.EditChatHistory(thread =>
                        {
                            if (thread.ChatHistory.Count > 0)
                                thread.ChatHistory[0].Content = rebuilt;
                        }, "ContextManager 还原系统提示词");
                    }

                    SendOverrideState(owner);
                    QueueInitialState();
                }
                catch (Exception ex) { SendWindow("error", new { message = "清除覆盖失败：" + ex.Message }); }
            }
        });
    }

    void SendOverrideState(string owner)
    {
        SendWindow("override-state", new
        {
            owner,
            mode = Config.OverrideMode ?? "Off",
            activePreset = Config.ActivePreset ?? "",
            useCharacterCard = Config.UseCharacterCard,
            useWorldBook = Config.UseWorldBook,
            applyMacros = Config.ApplyMacros,
            hasTemp = ReadTempOverride(owner) != null
        });
    }

    // 框架注入的官方系统消息（index[0]）。装配页的「角色设定 #0」模块保存的就是这段全文，
    // 因此插件覆盖下用户可以把名称/生日/简介/设定/私人文件夹整段一起改写。
    // 具体构造在 ContextPromptText 里（不依赖框架类型，可单元测试）。
    internal static string BuildOfficialSystemPrompt(Character character, string? storageRoot)
        => ContextPromptText.Build(character.Name, character.Birthday, character.Description, character.Prompt,
            character.StorageKey, storageRoot);

    // 从官方系统消息里取出「设定」正文；内容不是官方外壳（用户自定义）时原样返回。
    // 本地覆盖写回 index.json 的 Prompt 时必须用它：框架会再套一层同样的外壳。
    internal static string ExtractPromptBody(string? text) => ContextPromptText.ExtractPromptBody(text);

    Character ReadActiveCharacterFromDisk(Character character, string? storageRoot)
    {
        try
        {
            var dir = ResolveCharacterDirectory(character, storageRoot);
            var path = dir == null ? null : Path.Combine(dir, "index.json");
            if (path != null && File.Exists(path))
                return JObject.Parse(File.ReadAllText(path)).ToObject<Character>() ?? character;
        }
        catch (Exception ex) { logger.LogWarning(ex, "读取当前角色 index.json 失败 {Owner}", character.Name); }
        return character;
    }


    string GetPresetDirectory()
    {
        var root = FindStorageRoot() ?? throw new InvalidOperationException("找不到 Storage 根目录");
        var dir = Path.Combine(root, "ContextManager", "Presets");
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ---------------------------------------------------------------------
    // 角色预设库：每个角色一份上下文记录。
    //   Storage/ContextManager/CharacterPresets/<角色>.json            ← 自动槽（应用/保存时自动更新）
    //   Storage/ContextManager/CharacterPresets/<角色>__<名称>.json     ← 用户手动保存的命名快照
    // 计划文件（Plans/<角色>.json）丢了也能用自动槽恢复，所以插件一启用就能直接生效。
    // ---------------------------------------------------------------------
    string GetCharacterPresetDirectory()
    {
        var root = FindStorageRoot() ?? throw new InvalidOperationException("找不到 Storage 根目录");
        var dir = Path.Combine(root, "ContextManager", "CharacterPresets");
        Directory.CreateDirectory(dir);
        return dir;
    }

    string CharacterPresetPath(string owner, string? name)
    {
        var dir = GetCharacterPresetDirectory();
        var ownerKey = SafeFileName(owner);
        // 名称为空，或名称就是角色名 → 自动槽。
        if (string.IsNullOrWhiteSpace(name) || string.Equals(name.Trim(), owner, StringComparison.OrdinalIgnoreCase))
            return Path.Combine(dir, ownerKey + ".json");
        return Path.Combine(dir, ownerKey + "__" + SafeFileName(name.Trim()) + ".json");
    }

    // ---- 快照里的「设定来源」-------------------------------------------------
    // 一份角色快照要能「一个人带走全部设定」。光存装配计划只是配方：模块正文里大多是
    // {{description}} / {{worldbook}} 这类宏，取值来自角色目录的 TavernCard.json /
    // WorldBook.json 和 ContextManager/Presets/<名>.json。原料不跟着走，换角色/换机器就散架。
    static JObject? ReadJsonObjectOrNull(string path)
    {
        try { return File.Exists(path) ? JObject.Parse(File.ReadAllText(path)) : null; }
        catch (Exception ex) { ContextTrace.Write($"preset source read failed {path}: {ex.Message}"); return null; }
    }

    static string Fingerprint(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? $"{info.Name}|{info.Length}|{info.LastWriteTimeUtc.Ticks}" : $"{Path.GetFileName(path)}|missing";
        }
        catch { return $"{Path.GetFileName(path)}|error"; }
    }

    /// <summary>
    /// 抓取当前角色目录下的世界书 / 酒馆卡，以及当前选中的酒馆预设原文。
    /// <paramref name="previous"/> 是同一份快照上一次抓到的结果：指纹一致时直接复用，
    /// 避免「每勾选一个模块就重新解析一份 1 MB 的角色卡」。
    /// </summary>
    CharacterPresetSources? CapturePresetSources(string owner, CharacterPresetSources? previous = null)
    {
        var activePreset = (Config.ActivePreset ?? "").Trim();
        string? dir = null;
        try { dir = ResolveCharacterDirectory(GetCharacterRequired(owner), FindStorageRoot()); }
        catch (Exception ex) { ContextTrace.Write($"preset sources(character) failed owner={owner}: {ex.Message}"); }

        var worldBookPath = string.IsNullOrWhiteSpace(dir) ? null : Path.Combine(dir, "WorldBook.json");
        var cardPath = string.IsNullOrWhiteSpace(dir) ? null : Path.Combine(dir, "TavernCard.json");
        string? presetPath = null;
        if (activePreset.Length > 0)
        {
            try { presetPath = Path.Combine(GetPresetDirectory(), SafeFileName(activePreset) + ".json"); }
            catch (Exception ex) { ContextTrace.Write($"preset sources(preset) failed owner={owner}: {ex.Message}"); }
        }

        var fingerprints = new List<string>();
        foreach (var path in new[] { worldBookPath, cardPath, presetPath })
            if (!string.IsNullOrWhiteSpace(path)) fingerprints.Add(Fingerprint(path!));
        fingerprints.Add("preset|" + activePreset);
        fingerprints.Sort(StringComparer.Ordinal);

        if (previous != null && previous.Fingerprints.Count == fingerprints.Count
            && previous.Fingerprints.SequenceEqual(fingerprints, StringComparer.Ordinal))
            return previous;

        var sources = new CharacterPresetSources
        {
            CapturedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            ActivePreset = activePreset,
            Fingerprints = fingerprints
        };
        if (worldBookPath != null) sources.WorldBook = ReadJsonObjectOrNull(worldBookPath);
        if (cardPath != null) sources.TavernCard = ReadJsonObjectOrNull(cardPath);
        if (presetPath != null) sources.Preset = ReadJsonObjectOrNull(presetPath);
        return sources.IsEmpty ? null : sources;
    }

    /// <summary>
    /// 把快照里的设定来源写回角色目录。
    ///
    /// 默认**只补缺失的文件**（overwrite:false）：角色目录里已有的 WorldBook.json /
    /// TavernCard.json 可能是用户在本机改过的，静默覆盖等于丢数据。
    /// 返回给用户看的一句话说明；什么都没做时返回 null。
    /// </summary>
    string? RestorePresetSources(string owner, CharacterPresetSources? sources, bool overwrite = false)
    {
        if (sources == null || sources.IsEmpty) return null;
        var notes = new List<string>();
        try
        {
            var dir = ResolveCharacterDirectory(GetCharacterRequired(owner), FindStorageRoot());
            if (!string.IsNullOrWhiteSpace(dir))
            {
                var bookLabel = sources.WorldBookEntries > 0 ? $"世界书 {sources.WorldBookEntries} 条" : "世界书";
                WritePresetSource(dir, "WorldBook.json", sources.WorldBook, overwrite, notes, bookLabel);
                WritePresetSource(dir, "TavernCard.json", sources.TavernCard, overwrite, notes, "酒馆角色卡");
            }
        }
        catch (Exception ex) { ContextTrace.Write($"preset sources restore failed owner={owner}: {ex.Message}"); return null; }
        // 酒馆预设：只在当前没有选中任何预设时才替用户选上 —— 用户手选的预设不该被快照改掉。
        try
        {
            if (!string.IsNullOrWhiteSpace(sources.ActivePreset) && string.IsNullOrWhiteSpace(Config.ActivePreset) && sources.Preset != null)
            {
                AtomicWrite(Path.Combine(GetPresetDirectory(), SafeFileName(sources.ActivePreset) + ".json"), sources.Preset.ToString(Formatting.Indented));
                Config.ActivePreset = sources.ActivePreset;
                notes.Add($"酒馆预设「{sources.ActivePreset}」");
                ListPresets();
            }
        }
        catch (Exception ex) { ContextTrace.Write($"preset sources restore preset failed owner={owner}: {ex.Message}"); }
        if (notes.Count == 0) return null;
        ContextTrace.Write($"preset sources restored owner={owner} -> {string.Join("、", notes)}");
        return "随快照带回：" + string.Join("、", notes);
    }

    static void WritePresetSource(string dir, string fileName, JObject? payload, bool overwrite, List<string> notes, string label)
    {
        if (payload == null) return;
        var path = Path.Combine(dir, fileName);
        if (File.Exists(path) && !overwrite) return;
        if (File.Exists(path)) File.Copy(path, path + ".backup-" + DateTime.Now.ToString("yyyyMMddHHmmss"), true);
        AtomicWrite(path, payload.ToString(Formatting.Indented));
        notes.Add(label);
    }

    /// <param name="sourcesOverride">
    /// 显式指定快照要存的「设定来源」。导入角色记录时必须传它 —— 否则
    /// CapturePresetSources 会把**本机当前**的 WorldBook.json / TavernCard.json 写进快照，
    /// 把刚导入进来的那份覆盖掉。
    /// </param>
    void SaveCharacterPresetInternal(string owner, string? name, ContextPlan plan, bool auto, CharacterPresetSources? sourcesOverride = null)
    {
        var file = CharacterPresetPath(owner, auto ? owner : name);
        // 自动快照每次保存方案都会被调用，所以先读一次旧快照拿指纹，源文件没变就不重抓。
        CharacterPresetSources? previous = null;
        try
        {
            if (File.Exists(file))
                previous = JsonConvert.DeserializeObject<CharacterContextPreset>(File.ReadAllText(file))?.Sources;
        }
        catch (Exception ex) { ContextTrace.Write($"character preset previous read failed owner={owner}: {ex.Message}"); }
        var preset = new CharacterContextPreset
        {
            Owner = owner,
            Name = auto || string.IsNullOrWhiteSpace(name) ? owner : name.Trim(),
            Auto = auto,
            UpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            Plan = plan,
            Sources = sourcesOverride ?? CapturePresetSources(owner, previous)
        };
        AtomicWrite(file, JsonConvert.SerializeObject(preset, SnapshotJsonSettings));
        ContextTrace.Write($"character preset saved owner={owner} name={preset.Name} auto={auto} mode={plan?.Mode} worldbook={preset.Sources?.WorldBookEntries ?? 0} card={preset.Sources?.TavernCard != null}");
    }

    // 每次应用/保存计划后，自动更新该角色的同名快照。
    void AutoSaveCharacterPreset(string owner, ContextPlan? plan)
    {
        if (plan == null) return;
        try { SaveCharacterPresetInternal(owner, null, plan, true); }
        catch (Exception ex) { ContextTrace.Write("auto character preset failed owner=" + owner + " : " + ex.Message); }
    }

    // 计划文件不存在时，用自动槽恢复（例如 Plans 目录被清理、或换了机器只带走了角色预设）。
    bool TryRestoreCharacterPreset(string owner)
    {
        try
        {
            var planFile = Path.Combine(PlanDirectory(), SafeFileName(owner) + ".json");
            if (File.Exists(planFile)) return false;
            var file = CharacterPresetPath(owner, null);
            if (!File.Exists(file)) return false;
            var preset = JsonConvert.DeserializeObject<CharacterContextPreset>(File.ReadAllText(file));
            if (preset?.Plan == null) return false;
            ContextCompiler.Validate(preset.Plan);
            GetPlanService().SavePlan(owner, preset.Plan);
            // 计划文件都没了，角色目录里的世界书 / 酒馆卡很可能也不在（换机器只带走了快照）。
            // 只补缺失的文件，不覆盖本机已有的。
            RestorePresetSources(owner, preset.Sources);
            ContextTrace.Write($"character preset restored owner={owner} mode={preset.Plan.Mode} modules={preset.Plan.Modules.Count}");
            return true;
        }
        catch (Exception ex) { ContextTrace.Write("character preset restore failed owner=" + owner + " : " + ex.Message); return false; }
    }

    void ListCharacterPresets(string owner)
    {
        try
        {
            var dir = GetCharacterPresetDirectory();
            var ownerKey = SafeFileName(owner);
            var slots = new List<CharacterPresetInfo>();
            foreach (var file in Directory.GetFiles(dir, "*.json"))
            {
                var baseName = Path.GetFileNameWithoutExtension(file);
                var isAuto = string.Equals(baseName, ownerKey, StringComparison.OrdinalIgnoreCase);
                var isNamed = baseName.StartsWith(ownerKey + "__", StringComparison.OrdinalIgnoreCase);
                if (!isAuto && !isNamed) continue;
                try
                {
                    var preset = JsonConvert.DeserializeObject<CharacterContextPreset>(File.ReadAllText(file));
                    if (preset == null) continue;
                    slots.Add(new CharacterPresetInfo
                    {
                        Name = string.IsNullOrWhiteSpace(preset.Name) ? (isAuto ? owner : baseName[(ownerKey.Length + 2)..]) : preset.Name,
                        Auto = preset.Auto || isAuto,
                        UpdatedAt = preset.UpdatedAt,
                        Mode = preset.Plan?.Mode ?? "Off",
                        Modules = preset.Plan?.Modules?.Count ?? 0,
                        WorldbookEntries = preset.Sources?.WorldBookEntries ?? 0,
                        HasTavernCard = preset.Sources?.TavernCard != null,
                        ActivePreset = preset.Sources?.ActivePreset ?? ""
                    });
                }
                catch (Exception ex) { ContextTrace.Write("character preset parse failed " + file + " : " + ex.Message); }
            }
            SendWindow("charpreset-list", new { owner, slots = slots.OrderByDescending(s => s.Auto).ThenBy(s => s.Name).ToList() });
        }
        catch (Exception ex) { SendWindow("error", new { message = "读取角色预设失败：" + ex.Message }); }
    }

    void SaveCharacterPresetFromUi(string owner, JsonElement payload) => PlanOperation(owner, () =>
    {
        var plan = PreparePlan(owner, payload);
        var name = JsonString(payload, "name");
        var auto = string.IsNullOrWhiteSpace(name) || string.Equals(name.Trim(), owner, StringComparison.OrdinalIgnoreCase);
        SaveCharacterPresetInternal(owner, name, plan, auto);
        ListCharacterPresets(owner);
        SendWindow("charpreset-saved", new { owner, name = auto ? owner : name!.Trim(), auto });
    });

    void LoadCharacterPreset(string owner, string name, bool apply) => PlanOperation(owner, () =>
    {
        var file = CharacterPresetPath(owner, name);
        if (!File.Exists(file)) throw new FileNotFoundException("角色预设不存在：" + (string.IsNullOrWhiteSpace(name) ? owner : name), file);
        var preset = JsonConvert.DeserializeObject<CharacterContextPreset>(File.ReadAllText(file))
            ?? throw new InvalidOperationException("角色预设内容无效");
        var plan = preset.Plan ?? throw new InvalidOperationException("角色预设里没有装配计划");
        ContextCompiler.Validate(plan);
        ContextTrace.Write($"character preset load owner={owner} name={preset.Name} apply={apply} mode={plan.Mode}");
        // 快照里带着设定来源（酒馆预设 / 世界书 / 酒馆卡）时一并带回；只补缺失的文件。
        var sourcesNote = RestorePresetSources(owner, preset.Sources);
        if (apply) ApplyPlanCore(owner, plan);
        else
        {
            // 只读取到编辑器：用户可以先检查再决定是否应用。
            GetPlanService().SavePlan(owner, plan);
            SendPlan(owner, plan);
        }
        if (sourcesNote != null) QueueInitialState();
        SendWindow("charpreset-loaded", new { owner, name = preset.Name, applied = apply, mode = plan.Mode, sources = sourcesNote });
    });

    void DeleteCharacterPreset(string owner, string name)
    {
        try
        {
            var file = CharacterPresetPath(owner, name);
            if (File.Exists(file)) File.Delete(file);
            ContextTrace.Write($"character preset deleted owner={owner} name={name}");
            SendWindow("charpreset-deleted", new { owner, name });
            ListCharacterPresets(owner);
        }
        catch (Exception ex) { SendWindow("error", new { message = "删除角色预设失败：" + ex.Message }); }
    }

    // ---------------------------------------------------------------------
    // 角色预设导出 / 导入：一个 .json 文件 = 一个角色的一整份上下文记录。
    // 换机器、备份、或者把 A 角色的方案搬到 B 角色，都走这两个入口。
    // ---------------------------------------------------------------------
    async void ExportCharacterPreset(string owner, JsonElement payload)
    {
        try
        {
            var name = JsonString(payload, "name");
            var hasPlan = payload.TryGetProperty("plan", out var planElement) && planElement.ValueKind == JsonValueKind.Object;
            CharacterContextPreset preset;
            if (hasPlan)
            {
                // 直接导出编辑器里当前这一份（用户可能还没保存成快照）。
                var plan = JsonConvert.DeserializeObject<ContextPlan>(planElement.GetRawText()) ?? throw new InvalidOperationException("当前方案无效");
                ContextCompiler.Validate(plan);
                preset = new CharacterContextPreset
                {
                    Owner = owner,
                    Name = string.IsNullOrWhiteSpace(name) || string.Equals(name.Trim(), owner, StringComparison.OrdinalIgnoreCase) ? owner : name.Trim(),
                    Auto = false,
                    UpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    Plan = plan,
                    // 导出「编辑器里这一份」时也要带上设定来源，否则导出的文件只有配方没有原料。
                    Sources = CapturePresetSources(owner)
                };
            }
            else
            {
                var file = CharacterPresetPath(owner, name);
                if (!File.Exists(file)) throw new FileNotFoundException("角色预设不存在：" + (string.IsNullOrWhiteSpace(name) ? owner : name), file);
                preset = JsonConvert.DeserializeObject<CharacterContextPreset>(File.ReadAllText(file))
                    ?? throw new InvalidOperationException("角色预设内容无效");
                if (preset.Plan != null) ContextCompiler.Validate(preset.Plan);
            }

            var parent = windowService?.Window;
            if (parent == null) throw new InvalidOperationException("没有可用的插件窗口");
            var suggested = CharacterPresetTransfer.SuggestedFileName(owner, preset.Name, SafeFileName);
            ContextTrace.Write($"character preset export dialog owner={owner} name={preset.Name}");
            var target = await Electron.Dialog.ShowSaveDialogAsync(parent, new SaveDialogOptions
            {
                Title = "导出角色上下文记录",
                ButtonLabel = "导出",
                DefaultPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), suggested),
                Filters = new[] { new FileFilter { Name = "角色上下文记录", Extensions = new[] { "json" } } }
            });
            ContextTrace.Write($"character preset export result={target ?? "<empty>"}");
            if (string.IsNullOrWhiteSpace(target))
            {
                SendWindow("charpreset-export-cancelled", new { owner });
                return;
            }
            if (!target.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) target += ".json";
            AtomicWrite(target, JsonConvert.SerializeObject(preset, SnapshotJsonSettings));
            ContextTrace.Write($"character preset exported owner={owner} -> {target}");
            SendWindow("charpreset-exported", new
            {
                owner,
                path = target,
                name = preset.Name,
                modules = preset.Plan?.Modules?.Count ?? 0,
                worldbook = preset.Sources?.WorldBookEntries ?? 0,
                card = preset.Sources?.TavernCard != null,
                activePreset = preset.Sources?.ActivePreset ?? ""
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "导出角色预设失败 {Owner}", owner);
            ContextTrace.Write("character preset export FAILED: " + ex.Message);
            SendWindow("plan-error", new { owner, message = "导出角色预设失败：" + ex.Message });
        }
    }

    // 兼容「裸计划文件」：把 JSON 直接按 ContextPlan 解析。解析不出来就返回 null。
    static ContextPlan? TryParseBarePlan(string text)
    {
        try { return JsonConvert.DeserializeObject<ContextPlan>(text); }
        catch { return null; }
    }

    async void ImportCharacterPreset(string owner)
    {
        try
        {
            var parent = windowService?.Window;
            if (parent == null) throw new InvalidOperationException("没有可用的插件窗口");
            ContextTrace.Write($"character preset import dialog owner={owner}");
            var files = await Electron.Dialog.ShowOpenDialogAsync(parent, new OpenDialogOptions
            {
                Title = "导入角色上下文记录（会落到当前选中的角色）",
                ButtonLabel = "导入",
                Filters = new[] { new FileFilter { Name = "角色上下文记录", Extensions = new[] { "json" } } },
                Properties = new[] { OpenDialogProperty.openFile }
            });
            var file = files?.FirstOrDefault();
            ContextTrace.Write($"character preset import result={file ?? "<empty>"}");
            if (string.IsNullOrWhiteSpace(file))
            {
                SendWindow("charpreset-import-cancelled", new { owner });
                return;
            }

            var text = File.ReadAllText(file);
            // 注意：CharacterContextPreset.Plan 有 `= new()` 初始化，反序列化一个没有 plan 的
            // 文件不会得到 null，而是「0 个模块的空计划」。所以这里按模块数量判断，
            // 顺便兼容「裸计划文件」（直接导出 Plans/<角色>.json 的情况）。
            var preset = JsonConvert.DeserializeObject<CharacterContextPreset>(text);
            if (preset?.Plan == null || preset.Plan.Modules.Count == 0)
            {
                var bare = TryParseBarePlan(text);
                if (bare != null && bare.Modules.Count > 0)
                    preset = new CharacterContextPreset { Owner = owner, Name = Path.GetFileNameWithoutExtension(file), Plan = bare };
            }
            if (preset?.Plan != null && preset.Plan.Modules.Count > 0) ContextCompiler.Validate(preset.Plan);

            // 导入 = 换主人：文件里记的 owner 可能是别的角色，这里一律归到当前角色。
            if (preset == null) throw new InvalidOperationException("这个文件既不是角色上下文记录，也不是一份装配计划（JSON 结构对不上）");
            var sourceOwner = preset.Owner ?? "";
            var rebound = CharacterPresetTransfer.Rebind(preset, owner, Path.GetFileNameWithoutExtension(file));
            string? sourcesNote = null;
            PlanOperation(owner, () =>
            {
                // 先把随快照带回的设定来源落到角色目录（只补缺失的文件），再存快照本身 ——
                // 顺序不能反：SaveCharacterPresetInternal 会读角色目录来补全 Sources。
                sourcesNote = RestorePresetSources(owner, rebound.Sources);
                SaveCharacterPresetInternal(owner, rebound.Name, rebound.Plan!, false, rebound.Sources);
                ListCharacterPresets(owner);
                if (sourcesNote != null) QueueInitialState();
            });
            ContextTrace.Write($"character preset imported owner={owner} from={file} sourceOwner={sourceOwner} name={rebound.Name} modules={rebound.Plan!.Modules.Count} worldbook={rebound.Sources?.WorldBookEntries ?? 0}");
            SendWindow("charpreset-imported", new { owner, name = rebound.Name, from = sourceOwner, path = file, modules = rebound.Plan!.Modules.Count, sources = sourcesNote });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "导入角色预设失败 {Owner}", owner);
            ContextTrace.Write("character preset import FAILED: " + ex.Message);
            SendWindow("plan-error", new { owner, message = "导入角色上下文记录失败：" + ex.Message });
        }
    }

    void ListPresets()
    {
        try
        {
            var names = Directory.GetFiles(GetPresetDirectory(), "*.json")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .OrderBy(n => n)
                .ToList();
            SendWindow("presets-list", new { names });
        }
        catch (Exception ex) { SendWindow("error", new { message = "读取预设列表失败：" + ex.Message }); }
    }

    void GetPreset(string name)
    {
        _ = Task.Run(() =>
        {
            try
            {
                var file = Path.Combine(GetPresetDirectory(), SafeFileName(name) + ".json");
                if (!File.Exists(file)) throw new FileNotFoundException("预设不存在", file);
                var jo = JObject.Parse(File.ReadAllText(file));
                SendWindow("preset-state", new { name, preset = jo });
            }
            catch (Exception ex) { SendWindow("error", new { message = "读取预设失败：" + ex.Message }); }
        });
    }

    void SavePreset(string name, JsonElement payload)
    {
        _ = Task.Run(() =>
        {
            try
            {
                var finalName = string.IsNullOrWhiteSpace(name) ? Guid.NewGuid().ToString("N") : SafeFileName(name);
                var file = Path.Combine(GetPresetDirectory(), finalName + ".json");
                JObject preset;
                if (payload.TryGetProperty("preset", out var presetElement) && presetElement.ValueKind == JsonValueKind.Object)
                    preset = JObject.Parse(presetElement.GetRawText());
                else preset = new JObject();
                preset["Name"] = finalName;
                preset["UpdatedAt"] = DateTime.Now;
                File.WriteAllText(file, preset.ToString());
                SendWindow("preset-state", new { name = finalName, preset });
                ListPresets();
            }
            catch (Exception ex) { SendWindow("error", new { message = "保存预设失败：" + ex.Message }); }
        });
    }

    void DeletePreset(string name)
    {
        _ = Task.Run(() =>
        {
            try
            {
                var file = Path.Combine(GetPresetDirectory(), SafeFileName(name) + ".json");
                if (File.Exists(file)) File.Delete(file);
                SendWindow("preset-deleted", new { name });
                ListPresets();
            }
            catch (Exception ex) { SendWindow("error", new { message = "删除预设失败：" + ex.Message }); }
        });
    }

    void ImportTavernPreset()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var parent = windowService?.Window ?? Electron.WindowManager.BrowserWindows.FirstOrDefault()
                    ?? throw new InvalidOperationException("没有可用的插件窗口");
                var options = new OpenDialogOptions
                {
                    Title = "选择酒馆预设 JSON",
                    ButtonLabel = "导入",
                    Filters = new[] { new FileFilter { Name = "酒馆预设", Extensions = new[] { "json" } } },
                    Properties = new[] { OpenDialogProperty.openFile }
                };
                var files = await Electron.Dialog.ShowOpenDialogAsync(parent, options);
                var file = files.FirstOrDefault() ?? throw new InvalidOperationException("未选择文件");
                var root = JObject.Parse(File.ReadAllText(file));
                var source = root["prompts"] as JArray ?? new JArray();
                if (source.Count == 0)
                {
                    // 部分预设把每个提示词放在根字段。保留可识别的字符串字段。
                    foreach (var prop in root.Properties().Where(p => p.Value.Type == JTokenType.String))
                        source.Add(new JObject { ["name"] = prop.Name, ["content"] = prop.Value.Value<string>() });
                }

                var entries = new JArray();
                var order = 0;
                foreach (var item in source.Cast<JObject>())
                {
                    var content = item.Value<string>("system_prompt") ?? item.Value<string>("content") ?? item.Value<string>("prompt") ?? "";
                    var name = item.Value<string>("name") ?? item.Value<string>("identifier") ?? $"Prompt {order + 1}";
                    var role = item.Value<string>("role") ?? "system";
                    var enabled = item.Value<bool?>("enabled") ?? true;
                    if (string.IsNullOrWhiteSpace(content)) continue;
                    entries.Add(new JObject
                    {
                        ["Id"] = Guid.NewGuid().ToString("N"),
                        ["Name"] = name,
                        ["Role"] = role,
                        ["Content"] = content,
                        ["Enabled"] = enabled,
                        ["Order"] = order
                    });
                    order++;
                }

                var baseName = Path.GetFileNameWithoutExtension(file);
                var finalName = UniquePresetName(baseName);
                var preset = new JObject
                {
                    ["Name"] = finalName,
                    ["SourceFormat"] = "SillyTavernPreset",
                    ["Entries"] = entries,
                    ["CreatedAt"] = DateTime.Now,
                    ["UpdatedAt"] = DateTime.Now
                };
                File.WriteAllText(Path.Combine(GetPresetDirectory(), finalName + ".json"), preset.ToString());
                SendWindow("preset-state", new { name = finalName, preset });
                ListPresets();
            }
            catch (Exception ex) { SendWindow("error", new { message = "导入酒馆预设失败：" + ex.Message }); }
        });
    }

    string UniquePresetName(string baseName)
    {
        baseName = SafeFileName(string.IsNullOrWhiteSpace(baseName) ? "Tavern Preset" : baseName);
        var dir = GetPresetDirectory();
        if (!File.Exists(Path.Combine(dir, baseName + ".json"))) return baseName;
        for (var i = 2; i < 1000; i++)
        {
            var candidate = $"{baseName}-{i}";
            if (!File.Exists(Path.Combine(dir, candidate + ".json"))) return candidate;
        }
        return baseName + "-" + Guid.NewGuid().ToString("N").Substring(0,6);
    }

    static string SafeFileName(string name)
    {
        var safe = Path.GetInvalidFileNameChars().Aggregate(name.Trim(), (a,c) => a.Replace(c,'_'));
        return string.IsNullOrWhiteSpace(safe) ? "Preset" : safe;
    }

    List<Character> GetCharacters()
    {
        var result = new List<Character>();
        try
        {
            result.AddRange(characterSystem.GetAllCharacters()
                .Where(c => !string.IsNullOrWhiteSpace(c.Name))
                .GroupBy(c => c.Name).Select(g => g.First()));
        }
        catch (Exception ex) { logger.LogWarning(ex, "读取 CharacterSystem 失败"); }

        var root = FindStorageRoot();
        var charRoot = root == null ? null : Path.Combine(root, "Character");
        try
        {
            if (Directory.Exists(charRoot))
            {
                foreach (var dir in Directory.GetDirectories(charRoot))
                {
                    var file = Path.Combine(dir, "index.json");
                    if (!File.Exists(file)) continue;
                    var jo = JObject.Parse(File.ReadAllText(file));
                    var name = jo.Value<string>("Name") ?? Path.GetFileName(dir);
                    if (result.Any(c => c.Name == name)) continue;
                    var modules = new HashSet<string>();
                    if (jo["Modules"] is JArray moduleArray)
                    {
                        foreach (var moduleName in moduleArray.Select(t => t?.ToString()))
                            if (!string.IsNullOrWhiteSpace(moduleName)) modules.Add(moduleName);
                    }
                    result.Add(new Character
                    {
                        Name = name,
                        Birthday = jo.Value<DateTime?>("Birthday") ?? DateTime.Now,
                        Description = jo.Value<string>("Description") ?? "",
                        Prompt = jo.Value<string>("Prompt") ?? "",
                        Modules = modules
                    });
                }
            }
        }
        catch (Exception ex) { logger.LogWarning(ex, "扫描角色目录失败"); }
        return result;
    }

    string? FindStorageRoot()
    {
        try
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "Storage");
                if (Directory.Exists(Path.Combine(candidate, "Character"))) return candidate;
                dir = dir.Parent;
            }
        }
        catch { }
        // 兜底：交给框架解析存储目录。
        // 这里绝不能写死本机绝对路径 —— 换台机器、或用户改过存储位置，插件就失效了。
        try { return AlifePath.StorageFolderPath; } catch { return null; }
    }

    string? ResolveCharacterDirectory(Character character, string? storageRoot)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(character.StorageKey))
            {
                var p = Path.Combine(storageRoot ?? "", character.StorageKey);
                if (Directory.Exists(p)) return p;
            }
            var byName = Path.Combine(storageRoot ?? "", "Character", character.Name ?? "");
            return Directory.Exists(byName) ? byName : null;
        }
        catch { return null; }
    }

    static bool TryParseMemory(string content, out int level)
    {
        level = 0;
        var match = Regex.Match(content, @"^\[记忆存档\((\d+)-");
        if (!match.Success) return false;
        return int.TryParse(match.Groups[1].Value, out level);
    }

    string Limit(string text)
    {
        // 实时窗口由 MemoryService 保持在约 100 条有界范围内；文字完整展示，不再截断。
        // 可能爆量的 base64 图片/音频走懒加载，不进入此处文本。
        return text ?? "";
    }

    string PreviewLimit(string text, int maxChars)
    {
        text ??= "";
        if (maxChars <= 0 || text.Length <= maxChars) return text;
        return text[..maxChars] + "\r\n…[预览已截断，双击查看全文]";
    }

    static int EstimateTokens(string text)
        => string.IsNullOrEmpty(text) ? 0 : Math.Max(1, (int)Math.Ceiling(text.Length / 1.7));

    static string DetectTextModality(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return "text";
        if (Regex.IsMatch(content, @"<\s*(speak|expression|motion)\b", RegexOptions.IgnoreCase)) return "control+text";
        if (Regex.IsMatch(content, @"data:image|<\s*(img|image|audio|video)\b|https?://", RegexOptions.IgnoreCase)) return "multimodal";
        if (content.Contains("```") || content.Contains("using ") || content.Contains("def ")) return "code";
        return "text";
    }
}





