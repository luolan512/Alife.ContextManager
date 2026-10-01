#pragma warning disable SKEXP0001
#pragma warning disable SKEXP0110
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    //    ② `Release()`（插件卸载）以前不摘请求前重排的委托。那个委托是闭包，
    //       捕获了会话与运行时实例。插件重载而会话存活时：新运行时判定“已挂”而不再挂
    //       （插件覆盖静默指向旧实例），旧运行时还被闭包引用着回收不掉。
    //       `DetachTransforms()` 早就写好了却没人调用，现在在 Release 里补上。
    //       （第三十二轮起，摘除对象从 LanguageModel.ContextTransform 换成官方 ChatBot.ChatSent。）
    //    ③ 删掉 `int references;` —— 全项目只声明、从未读写。
    // 其余部分审计结论是**不会累积**：出站队列有界（256 条，满则丢新）、
    // 窗口/定时器在关窗与卸载时逐个释放、state 每次整份替换旧对象可回收、
    // 且界面全程是 DOM/CSS，没有 WebGL/canvas/video —— 显存占用恒定（只有一个窗口的合成开销）。
    // q：① **开放角色侧接口**（本轮）：插件第一次真正成为「角色能用」的插件 ——
    //       以前它只有界面，角色完全不知道它存在。现在加 `XmlFunctionCaller` 依赖
    //       （manifest 声明 Alife.Function.FunctionCaller，否则裸 Roslyn 编译时
    //       `Alife.Function` 命名空间根本不存在 —— 见 4.5.0 的 CS0234 教训），
    //       由 `ContextAgentApi` 注册 11 个函数：3 个只读（列目录 / 读正文 / 预览）、
    //       3 个增删改、4 个快照（列出 / 保存 / 删除 / 切换）、1 个「应用」。
    //       拆三层：`ContextAgentOps`（纯逻辑，与界面共用同一份计划文件）、
    //       `ContextAgentGuard`（准入 + 审批）、`ContextAgentApi`（函数外壳）。
    //    ② 配置页加两个总开关（`AllowModifySelf` / `AllowModifyOthers`，默认都关）：
    //       关着时 `BuildPrompt()` 返回空串，提示词里连「你有这个能力」都不出现。
    //       开着时也只放行「读」，每一次**写**仍然要用户在弹窗里点批准。
    //    ③ 修「配置页看不到任何配置项」：`ModuleDetailView.razor` 的规则是
    //       「有自定义 editorUI 就不再渲染自动表单」—— 本插件的 `ContextManagerUI`
    //       以前只画按钮、从不渲染 `DefaultUI`，所以 `OverrideMode` 等字段**从未在
    //       配置页露出过**。现在显式 `builder.AddContent(N, DefaultUI)`。
    //    ④ 修 `ContextAgentOps.NormalizeGroup` 的值域错误（测试抓到）：合法区域只有
    //       `system / features / memory / chat`（`ContextCompiler.Groups`），
    //       以前却用了 `user / assistant`，任何一次「新增模块」都会在
    //       `Validate` 抛「无效的模块区域」。现在把「身份」当「区域」说的输入统一折算，
    //       非法值兜底归 `system`。
    //    ⑤ 修 `ContextAgentOpsResult.With` 冲掉失败原因：失败结果补上下文时原先是**替换**
    //       Message，把 `Fail("没有传任何要修改的字段")` 覆盖成「模块 Id：…」，
    //       角色只能看到补充信息、看不到为什么失败。现在失败时改为「原因 + 补充」。
    // r：① **删掉逐次审批**（用户决定）。原先角色每改一次上下文都要用户在弹窗里点批准，
    //       与「配置页那两个开关」重复 —— 开关开着本身就是用户对信任的表达，
    //       已经做过一次的决定不该每次重问。现在只剩两处例外仍然会问（见 `ContextAgentGuard`）：
    //       跨角色**装插件**、以及**本地覆盖**（不可逆）。改自己永远安静。
    //    ② 连带取消「应用时询问用插件覆盖还是本地覆盖」：改自己一律插件覆盖（不动角色文件），
    //       改别人时若要走本地覆盖，在①那个弹窗里一次问清（多了个「不用插件」勾选框）。
    //    ③ **清理配置页五个死字段**：`OverrideMode` / `ActivePreset` / `UseCharacterCard` /
    //       `UseWorldBook` / `ApplyMacros`。它们全是 `ContextOverrideBuilder` 那条
    //       **已死路径**的输入 —— 那条路径唯一的入口 `ApplyOverride` 在 IPC 分派表里
    //       根本没有登记，前端也从不发对应消息。也就是说这五个开关改了什么都不会发生。
    //       其中 `UseCharacterCard` / `UseWorldBook` 还和模块化装配**互相打架**
    //       （「要不要带世界书」本来就是一个模块的 `Enabled`，两处开关用户不知道谁说了算）。
    //    ④ 删掉死代码：`ContextOverrideBuilder.cs`（约 300 行）、`ApplyOverride` /
    //       `GetOverrideBuilder` / `WriteTempOverride` / `ClearOverride` / `SendOverrideState`
    //       （全部零调用点）。保留 `DeleteTempOverride`（`ContextAssemblyRuntime` 在用它
    //       清理老路径留下的 `OverrideState/<角色>/context.txt`）。
    //       角色快照里的「设定来源」改为带上**预设目录下全部预设**（原先只带当时选中的那一份）。
    // s：**函数文档终于真的注入给 AI 了**（用户指出「具体函数标签文档没给全」）。
    //       根因：本模块用 `RegisterHandlerWithoutDocument` 注册 = `DocumentMode.None`，
    //       而 `XmlFunctionCaller.UpdatePrompt()` 只拼接 `explicitHandlers` / `implicitHandlers`，
    //       `None` **两边都不进** ⇒ 框架永远不会把这 11 个函数写进提示词。
    //       AI 只能看到手写的口语化能力描述（「能查看、能修改」），
    //       却不知道函数叫什么、参数叫什么、哪些可选、正文放标签里还是属性里 —— 只能猜。
    //       修法照抄工具箱（`Marisa.Toolkit`）的同一个位置：把 `handler.FunctionDocument()`
    //       直接拼进 `BuildPrompt()` 的「## 提供函数」一节。文档由框架从 `[XmlFunction]`
    //       + `[Description]` + 参数签名**自动渲染**，函数一改文档自动跟着变，不会手写不同步。
    //       同时补了「## 怎么用」一节（自闭合 vs 包裹写法、转义、编号从哪来）。
    // t：**「模块名与内容对不上」+ 幽灵模块 + 说明文字**（用户一次报了三件事）。
    //    ① 模块名/内容错位的根因：framework 模块与实时消息的**配对键是下标**——
    //       计划里存 `TargetIndex`（= 「它在 ChatHistory 数组里第几位」），前端又靠正则
    //       从标题里抠 `#N` 反算同一个下标。下标不是身份：对话历史一更新（前部被裁剪、
    //       中间插入新消息），同一条消息的下标就变了，而计划记的还是旧值 ——
    //       卡片上的**名字**读的是当前那条消息，**正文**读的却是计划里那条模块，
    //       于是配到了两条不同的消息。
    //       修法：给每条实时消息算一个**内容指纹** `AnchorKey`（`ComputeAnchorKey`，
    //       system 消息用「功能说明名」，其余用正文前缀哈希，**刻意不含下标**）；
    //       计划模块也带上它。配对时指纹优先，命中不到就认定「这是一条新消息」而不是
    //       退回下标去复用别人的模块。老计划（没有指纹）仍按下标配对，但命中后会**补写指纹**，
    //       下次就走稳定路径。涉及：`AddLiveContext` / `AddOfflineHistory` /
    //       `ContextAssemblyRuntime.PreparePlan` / `ContextCompiler.Compile` /
    //       前端 `findFrameworkModule`。
    //    ② 幽灵模块：计划里的 framework 模块以前**只增不减**。插件停用后它注入的
    //       「功能说明」消息从历史里消失，计划里那条模块却还在、还挂着「参与装配」——
    //       这正是用户说的「子代理插件的说明怎么一直都在我都没启用」。
    //       `PreparePlan` 现在会把「锚点已不在当前历史里」的 framework 模块剔掉
    //       （**只在角色已激活时做**：未激活时历史读不到，照删会把好计划清空）。
    //    ③ 说明文字：`[Module]` 那句「高效查看角色预设、系统提示词、记忆……」只列了
    //       「有什么」，没讲「干什么、怎么管」。改成讲清「把这次请求发了什么摊在一条时间线上，
    //       可逐条增删改 / 排序 / 开关参与装配 / 存快照」。另外窗口导航栏底部加了常驻的
    //       「怎么用这个插件」——一页讲清三种覆盖方式的区别与装配页操作，
    //       不必再去翻 README（说明不该藏在仓库里）。
    //    u：① **「传正文」的函数一律不可用**（用户报：「所有需要传正文的 Add / Update 不能用会报错」）。
    //       根因：`AgentContextAdd` / `AgentContextUpdate` 声明成了
    //       `[XmlFunction(FunctionMode.OneShot)]` + 一个 `[XmlContent] string content` 参数。
    //       但框架的模式校验是**位与**（`XmlHandler.Invoker`）：
    //         `CallMode` 是自闭合时要求 `(Mode & OneShot) != 0`；
    //         是包裹时要求 `(Mode & Content) != 0`。
    //       写成 `OneShot` ⇒ **只允许自闭合**，而自闭合根本装不进正文 ——
    //       一旦 AI 按唯一能带正文的包裹写法调用，就抛
    //       「调用 AgentContextAdd 标签的方式错误，应该使用单个自闭合标签调用」。
    //       另外 `ContentName` 只在 `Mode == FunctionMode.Content` 时才由框架设置，
    //       所以 `[XmlContent]` 参数在 OneShot 下永远不会被填值 —— 双重报废。
    //       修法（照抄框架自带的 `Alife.Function.FileService.Write` 与
    //       `Marisa.AgentCollab.SubAgentSpawn`）：改成 `FunctionMode.Content`，
    //       收 `XmlExecutorContext context`，正文取 `context.FullContent`
    //       （**不是** `context.Content` —— 流式解析下 Closing 时它是空的），
    //       并在开头加 `if (context.CallMode != CallMode.Closing) return;`
    //       （Content 模式会先触发 Opening/Content 好几次，只有 Closing 才是正文收完）。
    //       其余 9 个函数是真·无正文（list / read / preview / delete / presets / …），
    //       继续用 `OneShot` 自闭合，**不要**一起改 —— 改了它们就会要求包裹写法而更糟。
    //    ② 提示词里的「怎么写」跟着修：原文要求「正文里的 `<` `>` `&` 要写成 `&lt;` …」——
    //       那是 `[XmlContent]` 时代的约束；现在正文走 `[XmlForm]`/`FullContent`，
    //       是**原样收下的纯文本**，照旧说明去转义反而会把内容写坏。
    //       现在改成：**两种写法各自的适用范围**列清楚（哪些只能自闭合、哪两个能包正文）、
    //       正文「纯文本、不需要转义」、update「不写正文 = 不改正文」。
    //       文档由框架 `FunctionDocument()` 自动渲染，本来就不会不同步；会不同步的正是
    //       这种手写的补充说明，所以它必须和实现放在同一个改动里。
    //    v：① **功能说明仍在移位 / 互相覆盖**（用户第二轮反馈：「功能说明和功能模块还是会出现
    //       移位问题，而且有的还会被覆盖掉模块里的内容说明」）。
    //       根因是上一轮的指纹**不够唯一**：对 system 消息只取「功能说明里的模块名」当 token。
    //       但一个插件可以往历史里插**多条** `[功能说明(XXX)]`（Toolkit 按分层暴露就是每层一条），
    //       它们的模块名相同 ⇒ 指纹完全相同 ⇒ 配对用 `FirstOrDefault` 永远命中第一条：
    //          · 第 2、3 条的卡片显示的是第 1 条的正文 → 看起来「移位」；
    //          · 第 1 条模块的正文被反复取用、后面几条的内容读不出来 → 看起来「被覆盖」。
    //       修法：token 改成 `name:XXX#hash:abcd…` —— 名字用于**聚类**（同一模块的多条），
    //       正文哈希用于**区分同一条的前后稳定**。再加 `ComputeUniqueAnchorKeys`：
    //       整批算指纹，重复的按先后顺序追加 `~1` `~2` 消歧（两条逐字相同的消息也不再撞车）。
    //       ⚠️ 必须**整批一起算**，逐条各算各的等于没做 —— 调用点在 `AddLiveContext`。
    //    ② 配对从「取第一条」改成「按序号对齐」：`ResolveFrameworkIndex`（后端）与
    //       `findFrameworkModule` / `OrdinalOf`（前端与装配侧）现在都会数「这条是历史上
    //       第几条同源」，再取计划里第几条同源模块。旧格式指纹（没有 `#hash:`）仍然兼容，
    //       不必让用户重建计划。
    //    ③ **提示词精简**（用户：「上下文管理的标签的说明也优化一下，废话很多，关键信息不多」）。
    //       原来的「你能做的事 / 每个函数都是一个 xml 标签 / [可选] 参数可不写」这些都在
    //       框架自动渲染的函数文档里写着，重复一遍只会稀释真正的约束。现在只留文档里
    //       **生成不出来**的东西：调用形式（自闭合 vs 开闭包裹）、正文不用转义、
    //       update 不写正文的含义、编号从哪来、以及「改完必须 apply」。
    //    w：① **乱序问题仍在**（用户第三次反馈：「不行，乱序问题还是存在」）。
    //       根因不在指纹，而在 `ContextCompiler.Compile` 的**分段主循环**：它用
    //         `cursor = 1; while (cursor < N && source[cursor].Role == System) AddSource(cursor++);`
    //       顺序扫，隐含假设「所有 system 消息连续排在开头」。但 `Interactor.Prompt`
    //       把插件的功能说明**插在最后一个 system 之后** —— 一旦中途插入，后面的消息
    //       全部落进错误的分段（装配顺序乱掉）。更早还叠了第二个错：
    //       `AddSource` 用 `position > 0 && Role == System ? "features" : "chat"` 判归属，
    //       于是**记忆存档**被判成 features，它在计划里的改写被 `edit.Group != sourceGroup`
    //       静默丢弃。
    //       修法（用户选「彻底改成不依赖位置」）：新增纯函数
    //       `ContextPromptText.SourceGroup(index, role, text)`，**只看消息自身**：
    //         index 0 且 system → system（角色设定）
    //         `[功能说明(...)]` 开头 → features
    //         `[记忆存档(` 开头      → memory
    //         其余 system           → system（用户自加的 system 模块）
    //         非 system             → chat
    //       主循环改成「按区域分桶」：先给每条消息算归属，再按 system→features→memory→chat
    //       固定顺序输出，桶内保持原有先后。位置怎么变都不影响。
    //       前端 `frameworkItems` / `frameworkModuleCard` / `frameworkOverride` 同步改用
    //       `liveGroupOf`（同一套规则），不再靠 `messageIndex(i)>0` 猜。
    //    ② **AI 找不到上下文管理器的说明**（用户：「我也没找到有用的任何跟上下文管理器
    //       有关的模块提示词」）。根因是 `RefreshAgentPrompt` 用「开关状态快照」去重 ——
    //       它假定「调过一次 Prompt()，那段提示词就永远在历史里」。但 `Interactor.Prompt`
    //       把插入的消息对象缓存在内部字段 `promptContent` 里；那条被别处移走后缓存成
    //       **孤儿**，再 `Prompt()` 只写孤儿、历史里什么都不会出现。
    //       修法：改成**幂等对齐** —— 每轮比对「期望的提示词」与「历史里实际那段」
    //       （`PromptInHistory()` 按 `[功能说明(ContextManagerModule)]` 标题定位，
    //       不看下标），不一致才写。内容没变时不做任何多余动作。
    //    ③ **新增导入能力**（用户：「让 ai 可以自己导入角色卡世界书预设之类的」）。
    //       `AgentContextImport`（order 23，OneShot 自闭合；JSON 走 `json` 属性，
    //       不塞标签正文 —— 免得整段 JSON 转义错一个字就废）+ `ContextAgentOps.ParseImport`
    //       （**形状优先于声明**：AI 常把世界书说成「角色卡」，按内容认，矛盾才报错）
    //       + `ImportModules`（整批追加、名称+正文判重、一次落盘）。
    //       解析复用既有 `TavernImport`：角色卡 6+2 字段 / 额外开场白 / 卡内世界书 /
    //       预设 prompts+prompt_order / 世界书三种形状。
    //       **只解析成模块，不写回角色目录**（用户明确要求）—— 关掉插件即复原。
    //    x：用户第四次反馈，一次报了三件事，**前两件其实是同一条链上的两个环节**。
    //    ① **说明还是插不进去**（「你还是没有把上下文管理器的使用说明插入到上下文里，
    //       我还是没找到对应模块」）。上一轮我把「开关快照去重」改成「幂等对齐」，
    //       以为修好了 —— 其实**一点用都没有**，因为真正的墙在 `Interactor<T>` 内部：
    //         · `Prompt()` 只在 `promptContent == null` 时才会去历史里找注入点；
    //         · `Dispose()` 只把消息 `Remove` 出历史，**不清空 `promptContent` 字段**；
    //         · 于是 `promptContent` 成了「不在历史里、引用还在」的**孤儿**，
    //           之后每次 `Prompt()` 都跳过查找、直接往孤儿写 → 历史里永远没有这段说明。
    //         · 最致命的时序：`OnAwake` 里首次 `BuildPrompt` 可能返回空（Runtime 刚建好、
    //           配置未应用），于是走了 `Dispose()` 分支 —— **一开机就把唯一的写入通道弄丢**。
    //       修法：**彻底不走 `Interactor` 的提示词通道**，自己持有 `promptMessage`
    //       并用 `ChatBot.EditChatHistory` 增删改。判定只看两件事：
    //         「我自己记着的那个对象还在不在历史里（按引用比对）」+
    //         「历史里有没有标题行匹配的同类消息（认领上次运行 / 他处留下的）」。
    //       两者都不看下标，所以重排、裁剪、重启都能自愈。
    //       同时 `OnDestroy` 也改成自己摘除（`Interactor.Dispose()` 同样不可靠）。
    //    ② **界面顺序混乱，但打开之后是对的**。这条与①同行：`liveGroupOf` 用
    //       `messageIndex(i)===0` 判断角色设定，而 `messageIndex` 是**正则从标题里抠 `#N`**。
    //       那个 `#N` 就是历史下标 —— 对话一变就整体重排，且某些条目（分页补回的老报文）
    //       可能根本没有 `#N`，退化 `-1` 排到最前。于是列表顺序每次都在漂。
    //       而「打开之后是对的」正是因为内容配对走的是 `AnchorKey`（不含下标，稳定）。
    //       修法：`ContextItem` 新增 `LiveOrder`（后端采集时直接赋整数），前端
    //       `liveOrdinal(i)` 用它排序，**不再解析标题字符串**；`liveGroupOf` 的
    //       「是不是第 0 条」也改成看 `liveOrdinal`。老报文没有该字段时仍回退 `messageIndex`。
    //       另外 `sortPlanGroups` 补上**稳定的第二排序键**（原下标）——
    //       以前同区域内谁先谁后完全交给 `Array.sort` 的实现，看起来就是「模块在乱跳」。
    //    ③ **角色设定重复出现**（「运行的时候会把角色设定#0 在 Alife 运行时里那一块也出现一块」）。
    //       计划里那条 native 模块（角色设定 #0）与运行时第 0 条（live-system）是**同一个东西的
    //       两个视图**，以前在「按来源」视图里同时列出 → 看着像被注入了两次。
    //       修法：新增 `isRepresentedByPlan(i)`，装配视图**不再单列**运行时的第 0 条
    //       （它的内容由 native 模块代表；「上下文总览」等地方照常显示，不隐藏）。
    //    y：用户第五轮反馈（附截图 + 真实上下文），**名字与内容分属两个数据源**。
    //    ① **提示词明明插进去了，却「看着像没插」**：它被插在「最后一个 system 之后」——
    //       你的历史里系统消息全在开头，但**其它插件会陆续往中间插**，于是「最后一个 system」
    //       一直在变，提示词被插到所有系统模块的**最末尾**，离角色设定很远，
    //       一眼扫过去根本找不到，用户描述为「没插入进去」。
    //       修法：固定插在**角色设定（第 0 条）之后** —— 前面只有一条消息，没有可漂的余地。
    //    ② **模块名与内容错乱**（截图：`ContextManagerModule #3` 的内容是 QuickChat 的说明）。
    //       根因是**标题里的名字依赖位置**：`LiveSystemTitle(text, index)` 的 `index` 是
    //       「遍历整个 ChatHistory 的全局下标」，而前端 `frameworkDisplayName()` 把尾部 ` #N`
    //       去掉当模块名 —— 等于**把位置当成了名字**。位置一漂，名字就指认了另一条消息。
    //       而卡片**内容**走的是 anchorKey 配对（稳定）—— 两个数据源，必然错位。
    //       修法：① 标题序号改用「**system 消息自己的序号**」（与指纹消歧序号同源），
    //       真正的位置另由 `LiveOrder` 承载、不塞进标题；
    //       ② 前端卡片的**名字也取自配对到的模块**（与内容同源），配不到才退回运行时标题。
    //       > **铁律：同一个用户可见对象的名字与内容，必须来自同一个数据源。**
    // y：**"提示词根本没插进去"其实是"日志根本写不出去"**。排查时 grep trace.log 搜
    //    `agent prompt` 零命中，看起来像 RefreshAgentPrompt 没执行 —— 错的。
    //    `ContextTrace.Write` 在 `Configure(storageRoot)` 之前直接 return，而 `Configure`
    //    **只在 OpenWindowAsync 里调用** → OnAwake/OnUpdate 这些最关键阶段的日志全被**静默丢弃**；
    //    框架又把 OnAwake 的异常吃掉只记 AlifeLog（不进 trace）→
    //    **"插件崩了"和"插件正常"在 trace 里长得一模一样：都没有日志。**
    //    修法：① ContextTrace 加 `ConfigureFallback`（兜底到插件目录下的 startup-trace.log，
    //    绝不静默丢弃）；② OnAwake 第一件事就挂兜底目录，并全程留痕
    //    （start / runtime ok / handler registered / FAILED）；③ RefreshAgentPrompt 五条分支
    //    各留一条 trace，一次重启就能定位断点；④ OnAwake/OnUpdate 自带 try/catch 落 trace。
    //    > **教训：`grep 无命中` 只能证明「日志写出来了、里面没这句」，
    //    > 不能证明「代码没执行」—— 前提是那条日志路径本身是通的。**
    // z：⭐⭐ **「一个数字被两种语义共用」的彻底清算** —— 用户第六轮截图：
    //    两张 `VirtualWorldService` 卡片内容一字不差、位置 #4 / #5，且「一个带 #4 一个不带」。
    //    真因是 **y 之前的第五批只改了一半**：把标题里的 `#N` 从「全局数组下标」改成
    //    「第几条 system 消息」（对**显示**是对的），**却忘了还有 5 处在拿 `#N` 当数组下标**：
    //      FillRuntimeSystemModuleContents / ApplyLiveContentEdit / RemoveLiveMessage /
    //      DeleteHistoryItem / UpdateHistoryContent / ReadFullContent
    //    两者只在「所有 system 消息恰好连续排在开头」时才相等。角色设定之后一插入消息
    //    （本插件自己注入的说明就是），就整体错位 →
    //      ① 卡片正文**张冠李戴**（用错的 N 取到别人的正文）；
    //      ② 同名模块**看起来重复**（两条消息的 N 落在同一段正文上）；
    //      ③ **编辑 / 删除会改错、删错那条消息**（数据破坏）。
    //    修法：**位置与名字彻底分家**。
    //      · 标题 = **纯名字**（`LiveSystemTitle` 不再拼 `#N`，非 system 消息也改成「角色 · 摘要」）；
    //      · 位置 = `LiveOrder`（整数），新增 `LiveIndex(item)` 作为**唯一**取下标入口；
    //      · 所有下标调用点统一改走 `LiveIndex`，删掉 `MessageIndexFromTitle` 这份重复正则；
    //      · 前端 `liveOrdinal` / `messageIndex` 收敛成**同一个实现**（后者只是别名）。
    //    > **铁律：一个数字不要承载两种语义**（位置 vs 名字）。分开存、分开读。
    //    > 推论：凡是「显示序号」被当成「数据下标」用的地方，都要当成**数据破坏级**隐患审一遍。
    //    > 反面教训：第五批"改一半"比不改更危险 —— 它让显示对了，从而掩盖了数据侧已经错位。
    // ab：⭐⭐ **读运行时源码后，把「编辑/删除实时消息」从「信下标」改成「验指纹」** ——
    //    用户第三轮反馈「重复问题依然没解决」，截图里 `VirtualWorldService` 仍是两张
    //    （#3 576 字 / #4 597 字，字数差 = 两次注入时角色列表不同，即**两次 OnAwake**）。
    //    读 `Alife.Framework/Models/Module/Interactor.cs` 后确认：
    //      `Prompt()` 只在 `promptContent == null` 时查找复用，查找条件是
    //      `Content.StartsWith("[功能说明(VirtualWorldService)]")`；
    //    而 `ApplyLiveContentEdit` 走的是 `thread.ChatHistory[index].Content = content` ——
    //    **裸下标**。一旦下标错位（z 之前的历史遗留就是），就会把**别人**的提示词头改掉，
    //    于是那个插件重建 `Interactor` 后 Find 不到旧消息 → **又插一条** → 重复。
    //    修法：写入前用 `LiveMessageMatches(history, index, item)` 校验「这条确实是它」——
    //      · 有 `AnchorKey` → 用与装配计划**同一套** `ComputeAnchorKey` 严格比对；
    //      · 无指纹（老报文）→ 退化为「正文互为首缀」（Content 可能只是预览）；
    //      · 不符 → **抛错拒绝**，绝不硬改。删除路径同样校验。
    //    > **铁律：凡是「用下标定位别人的数据」，写入前必须验一次身份。**
    //    > 宁可不做，也不能做错 —— 做错会污染第三方插件的注入，且症状延迟出现、极难归因。
    // ae：⭐⭐ **AI 改了上下文模块，界面却不刷新** —— 补上「落盘后的通知」这条缺掉的路。
    //    用户反馈：AI 能新增/修改上下文模块，但**管理界面里不实时刷新**，
    //    **新模块根本不显示**；可是它确实存在（预览里看得见）。
    //    查证（全部有据）：
    //      · `Plans/momo.json` 里确实躺着 `测试模块A` / `测试模块B`（agent 来源、enabled、
    //        UpdatedAt 是几分钟前）——**模块真的写进去了**，不是没写成功；
    //      · `ContextAgentOps` 是**纯逻辑层**（类注释：不碰 IPC），构造里只有
    //        `planService`，改完只 `SavePlan`，**拿不到 runtime、发不出报文**；
    //      · `ContextAgentApi` 的三个写函数（add / update / delete）加上 import、
    //        切快照，共 **5 条写路径**，全部只落盘、零通知；
    //      · 界面这侧 `plan-state` / `state` 只在「用户自己保存 / 应用 / 历史被编辑」时推送。
    //    于是角色改完，磁盘变了，用户盯着的窗口纹丝不动 —— 必须关掉重开才看得到。
    //    修法：给 `ContextAgentOps` 加第 5 个构造参数 `onPlanChanged`（**由 Runtime 注入**），
    //      新增唯一落盘出口 `SaveAndNotify`：**先存盘、再通知**，通知抛异常只记日志。
    //      · 顺序不可颠倒 —— 通知方要读新计划，先通知会读到旧内容；
    //      · 只在实际改动时通知 —— 读操作（outline / read / preview）与失败的写不通知；
    //      · 通知失败**绝不能**把已经成功的改动报成失败（窗口没开时就是这种情况）。
    //      这三条都被 `agent-ops-tests/RefreshRegression.cs` 逐条锁定。
    //    > **铁律：写数据的层如果发不出通知，就必须把「通知」做成注入的出口，
    //    > 并且所有写路径共用一个落盘出口 —— 否则迟早漏掉一条路径（这里一次就漏了 5 条）。**
    //
    // af：⭐⭐ **「首次改别人成功，之后连给自己加模块都被拦」** —— 配置按角色隔离。
    //   现场：用户开了「允许角色修改自己/其他角色」两个开关，AI 改别的角色**第一次成功**，
    //   之后**所有**调用（含给自己加模块）都被回以「用户没有开启「允许角色修改其他角色的上下文」」。
    //   查证（全部实证）：
    //     ① 配置是**按角色**存的：`Storage/Character/<角色>/Configuration/Marisa.ContextManager.ContextManagerModule.json`。
    //        框架 `ChatActivity` 激活模块时按 `character.StorageKey` 逐个注入 `Configuration`
    //        （源码 `ChatActivity.cs:80-83`，注入发生在 `AwakeAsync` 之前）。
    //     ② 本项目 **5 个角色**装了本插件：momo（配置 = 两个开关都 true）、伊卡洛斯 / 小梦 /
    //        星野雨 / 酒狐（**无配置文件** → 框架 `Activator.CreateInstance` 出默认实例 → 两个都 false）。
    //     ③ **`ContextManagerRuntime` 是全局单例**（`current ??= new ...`），而 `Config` 是单例上的
    //        **一个**属性。老实现里 `ContextManagerModule.OnUpdate()` **每帧**都调
    //        `Runtime.ApplyConfig(Configuration)` —— 即把**本角色**的配置写进这个共享槽位。
    //     ⇒ 只要 momo 之外的任一角色在跑，`Config` 就被冲成全 false，momo 的权限被无声撤销。
    //        这**完整解释**了「首次成功、之后全拒」以及「连给自己加模块也被拦」（整份 Config 被冲掉）。
    //   修法（不改任何既有语义，只把「一个槽位」换成「按 owner 查」）：
    //     · 新增 `ownerConfigs` 字典 + 独立锁 `ownerConfigGate`（不与 stateLock 共用：
    //       它每帧都写、且不参与任何 IPC 构建，共用锁只会互相蹭）；
    //     · 新增 `SetOwnerConfig(owner, c)`（模块实例按 `SafeName()` 登记自己那份）与
    //       `ConfigFor(owner)`（取该角色自己的配置，查不到才回退兜底 `Config`）；
    //     · **准入判定**改读 `ConfigFor(request.SelfOwner)`（`CheckAgentAccessAsync`）——
    //       必须用「发起者自己」的配置，不是「最后写进来的那个角色的」；
    //     · **能力说明**同理改读 `ConfigFor(character.Name)`（`ContextAgentApi.BuildPrompt`），
    //       否则「AI 忽而知道自己能改、忽而又不知道」会随别人变；
    //     · 模块侧 `ApplyConfig(Configuration)` → `SetOwnerConfig(SafeName(), Configuration)`（OnAwake + OnUpdate 两处）。
    //   三条被 `agent-ops-tests` 的 10c.1~10c.10 逐条锁定；变异 `cfgshare` 退回共享槽位即被抓。
    //    > **铁律：全局单例上不要放「每个实例各不相同」的可变状态。**
    //    > 配置天然是 per-owner 的；把它摊到共享单例上，就等于让所有角色共用一个开关。
    //
    // ag：⭐⭐ **斩断对 OpenAI 插件私有接口 `ContextTransform` 的强依赖**。
    //   现场：应用计划时被回以「应用失败：当前语言模型没有 ContextTransform 接口，
    //   请先重载已更新的 OpenAI 语言模型插件。」—— 用户当场质疑「不是你该改什么 OpenAI 语言模型插件啊，
    //   如果强依附别人的插件，很麻烦的」。
    //   查证（全部实证）：
    //     ① **`ContextTransform` 不是官方 API。** Alife 官方源码（`Alife-master`）全库搜零命中；
    //        官方 `ILanguageModel`（`Alife.Framework/Models/ILanguageModel.cs`）**只有**
    //        `ChatStreamingAsync(...)` 一个方法，**没有任何请求前钩子**。
    //        那是**别人**给 OpenAI 插件源码硬加的私有属性（`edit.py` / `permanent-fix.py` 即其遗迹）。
    //     ② 一旦该插件被回退/替换/换模型，请求前重排整条链路**当场死掉**，还会把错误赖到用户身上
    //        （提示「请重载插件」）—— 这是「强依附别人插件」的必然恶果。
    //   修法（改用**官方**挂载点，零第三方依赖）：
    //     · 挂载对象从 `LanguageModel` 换成 `ChatBot`，从「反射写私有属性」换成「订阅公开事件」：
    //       `bot.ChatSent += handler`（`ChatBot.cs:36` `public event Action<string>? ChatSent`）。
    //     · 时序（`ChatBot.ChatAsync`）：`127` 装载用户消息 →`136` `ChatSent?.Invoke()` →`149` 发真请求。
    //       即：**用户消息已进历史、语言模型请求尚未发出** —— 正是重排的唯一正确窗口。
    //       `ChatSent` 触发时第 127 行那次 `EditChatHistoryAsync` 已释放 `chatHistorySemaphore`，
    //       所以回调里同步调 `ChatBot.EditChatHistory(...)`（`ChatBot.cs:75`，public 同步方法）**不死锁**。
    //     · 重排粒度：`bot.ChatHistory` 是对外**快照**（`IReadOnlyList<ChatMessageContent>`，第 54 行），
    //       而 `ContextCompiler.Compile` 要 `ChatHistory` —— 先按序拷一份喂编译，再在
    //       `EditChatHistory` 里「整体替换」（`Clear()` 后按编译结果重填）。
    //     · 订阅表键从「LanguageModel 对象」换成「ChatBot 对象」（`chatSentSubscriptions` + `chatSentGate`）：
    //       事件挂在 ChatBot 上，而 LanguageModel 可能在运行时被换（重载插件）→ 挂错对象必漏摘/漏挂。
    //     · **删掉**「挂不上就抛『没有 ContextTransform 接口』」那段 —— 官方事件任何语言模型都有，
    //       不存在「接口缺失」；挂不上只记日志，绝不打断对话。
    //     · `AttachTransform` 返回值改为「是否处于挂载态」，`TryAutoAttach` 不再看 `LanguageModel`。
    //   三条被冒烟测试锁定（不许再反射 ContextTransform / 必须 `bot.ChatSent += handler` /
    //   Release 仍摘 `DetachTransforms`）；变异 `chatsent` 退回反射即被抓。
    //    > **铁律：插件只能依赖官方公开 API。「别人插件里的私有口子」看着能用，**
    //    > **但对方一升级就断，还会把断的责任推给用户。**
    //
    // ah：⭐⭐⭐ **删除审批弹窗 —— 「等用户点弹窗」的准入机制会把整个对话卡死**。
    //   现场：AI 执行 `AgentContextAdd` 成功后，用户想继续对话，却发现发送通道被一直占住，
    //   界面上持续显示「执行 AgentContextAdd 函数丨分析对话」，**消息再也发不出去**。
    //   用户反馈的关键事实：**那个审批弹窗从来没有真正弹出来过**。
    //   根因：`CheckAgentAccessAsync` 在「跨角色」分支里 `await AskAgentAsync(...)` ——
    //   后者 `SendWindow("agent-ask", ...)` 后 `await` 一个 `TaskCompletionSource`。
    //   **弹窗没出现 ⇒ 用户不可能答 ⇒ TCS 永远不 SetResult ⇒ 那次工具调用永久挂起**
    //   （XmlHandler 的 Invoker 在 await 它），而工具调用不返回，框架的对话轮次就完不成，
    //   **整个会话的发送通道被这次调用占死**。
    //   ⚠️ 那个 `while(true) { await Task.WhenAny(tcs.Task, Task.Delay(5000)) }` 轮询是个
    //      **假保险**：它只在「插件窗口被关掉」时才 break；窗口一直开着但弹窗没显示，
    //      就永远转下去 —— 看上去"有超时"，实际仍然会永久挂住。
    //   修法（整体删除，不是修修补补）：
    //     · **去掉所有「等用户输入」的准入路径**。准入改成**同步方法**
    //       `string? CheckAgentAccess(AgentRequest, string)` —— 立即返回，不可能挂住。
    //     · 删除 `AskAgentAsync` / `AnswerAgentAsk` / `pendingAgentAsks` / `agentAskGate` /
    //       `AgentDecision`；8 个调用点从 `await runtime.CheckAgentAccessAsync(...)` 改同步。
    //     · 删除后端 `agent-answer` IPC 分支；删除前端 `showAgentAsk` / `answerAgentAsk` /
    //       `AGENT_RISK_NOTE` / `agent-ask` 弹窗分支（收到旧报文只记 trace，不弹窗）。
    //     · 权限模型简化成**一道闸**：配置页两个开关。插件覆盖未启用时，
    //       **改自己、改别人都直接自动启用**（插件覆盖只在请求前重排，不写任何角色文件，
    //       关掉插件即复原）—— 唯一被改的是插件自己的运行时状态，没有需要征求同意的地方。
    //   断言：10.4~10.20 与 12.1~12.9 全部反过来钉「不存在任何等用户输入的通道」；
    //   14.4 钉「后端不再分派 agent-answer」。变异 `approval` 退回 await 即被抓。
    //    > **铁律：任何「await 用户输入」的准入机制，只要「弹窗真的会出现」这个前提不成立，**
    //    > **就等价于一个必然卡死的陷阱 —— 卡住的代价是「整个对话不能用」，**
    //    > **远大于它想防住的风险。准入判定必须同步、立即、可预测。**
    public const string AppBuild = "cm-app-2026-10-02-ah";

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

    // ⭐⭐ 按角色（owner）登记的配置。
    //
    // 为什么必须有这个（2026-10-01 现场）：
    //   `ContextManagerRuntime` 是**全局单例**，而 `Config` 是单例上的**一个**属性。
    //   但配置是**按角色**存的（`Storage/Character/<角色>/Configuration/Marisa.ContextManager.ContextManagerModule.json`），
    //   每个装了插件的角色都有一份自己的 `Configuration`。
    //   老实现里 `OnUpdate()` **每帧**把本角色的 `Configuration` 写进共享的 `Config` ——
    //   于是「谁最后跑谁说了算」。本项目有 5 个角色装了插件（momo 开了权限，另外 4 个没配
    //   → 默认两个开关都是 false），只要 momo 之外的任一角色在跑，`Config` 就会被冲成全 false，
    //   momo 后续所有调用（**连给自己加模块**）都被拒 —— 正是用户报的「首次成功后全被拦」。
    //
    // 修法：**配置按 owner 查，不共享一个槽位**。`Config` 只作为「查不到时的兜底」。
    readonly Dictionary<string, ContextManagerConfig> ownerConfigs = new(StringComparer.OrdinalIgnoreCase);
    // 配置字典有**自己的**锁，不与 stateLock 共用：
    //   · 它不参与任何 IPC 数据构建，没有「持锁发送」的风险；
    //   · 且 OnUpdate 每帧都会写它 —— 若与 stateLock 共用，会和「锁内构建快照」互相蹭。
    readonly object ownerConfigGate = new();

    /// <summary>登记某个角色的插件配置（由该角色的模块实例在自己的 OnAwake / OnUpdate 里调用）。</summary>
    public void SetOwnerConfig(string? owner, ContextManagerConfig? c)
    {
        if (c == null) return;
        var key = owner ?? "";
        lock (ownerConfigGate) ownerConfigs[key] = c;
        // 兼容旧行为：兜底值也跟着更新（这样即使 owner 名对不上也有可用的默认）。
        Config = c;
    }

    /// <summary>
    /// 取某个角色**自己**的配置。查不到该角色的专属配置时回退到兜底 <see cref="Config"/>。
    /// <para>角色的准入判定必须用「发起者自己」的配置，而不是「最后写进来的那个角色的」。</para>
    /// </summary>
    public ContextManagerConfig ConfigFor(string? owner)
    {
        lock (ownerConfigGate)
        {
            if (!string.IsNullOrWhiteSpace(owner) && ownerConfigs.TryGetValue(owner!, out var c)) return c;
            return Config;
        }
    }

    /// <summary>兼容保留：把配置记为兜底值（不再作为唯一来源）。改用 <see cref="SetOwnerConfig"/>。</summary>
    public void ApplyConfig(ContextManagerConfig? c) { if (c != null) { Config = c; } }

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

    // ── 角色侧接口（2026-10-01 新增）──────────────────────────────────────
    // 角色通过 XmlHandler 调进来的操作层。它和界面共用同一个 ContextPlanService
    // 与同一份计划文件 —— 两条入口的落地动作必须一致，否则界面和角色的认知会分叉。
    ContextAgentOps? agentOps;

    /// <summary>角色侧操作层。首次访问时按当前运行时状态构建（Storage 根目录要运行时才拿得到）。</summary>
    internal ContextAgentOps AgentOps => agentOps ??= new ContextAgentOps(
        GetPlanService(),
        ReadIndexOrNull,
        RenderPlanForAgent,
        GetCharacterPresetDirectory(),
        // 第 5 个参数 = 「计划落盘了」的通知口。角色改完上下文后，界面那一侧不会自己知道，
        // 必须在这里把新计划推回窗口，否则用户看到的一直是旧样子（见 NotifyPlanChangedFromAgent）。
        NotifyPlanChangedFromAgent);

    /// <summary>ReadIndex 会抛异常（角色目录不存在 / index.json 坏了）；角色侧用不到异常，一律降级成 null。</summary>
    JObject? ReadIndexOrNull(string owner)
    {
        try { return ReadIndex(owner); }
        catch (Exception ex) { ContextTrace.Write($"agent read index failed owner={owner}: {ex.Message}"); return null; }
    }

    /// <summary>
    /// 把计划渲染成最终报文（供角色「预览」用）。
    /// 走的是与真实请求同一条 <see cref="ContextCompiler.Compile"/>，
    /// 只是历史消息用空的 —— 角色想看的是「上下文部分长什么样」，不是它正在聊的那轮对话。
    /// </summary>
    string RenderPlanForAgent(string owner, ContextPlan plan)
    {
        var index = ReadIndexOrNull(owner);
        var warnings = new List<string>();
        var history = new Microsoft.SemanticKernel.ChatCompletion.ChatHistory();
        // Off（关闭）模式下 Compile 会原样返回传入的 history —— 而这里传的是空 history，
        // 角色就会看到「什么都没有」。它想看的是「如果装配会发什么」，所以按 Temporary 渲染
        // （与界面「预览」按钮的处理完全一致，见 PreviewPlan）。
        var mode = plan.Mode;
        if (plan.Mode == "Off") plan.Mode = "Temporary";
        try
        {
            var messages = ContextCompiler.Compile(history, plan, index, owner, warnings);
            var sb = new System.Text.StringBuilder();
            if (mode == "Off")
                sb.AppendLine("（当前覆盖方式是「关闭」，下面是假设启用插件覆盖时会发出的内容）").AppendLine();
            foreach (var message in messages)
            {
                sb.Append('【').Append(ContextCompiler.Role(message.Role.Label)).Append("】\n");
                sb.AppendLine(message.Content);
                sb.AppendLine();
            }
            foreach (var warning in warnings)
                sb.AppendLine("（提示）" + warning);
            return sb.ToString().Trim();
        }
        finally { plan.Mode = mode; }
    }

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
        // ⚠️ 必须把挂在 ChatBot.ChatSent 上的委托摘掉。
        //
        // 那个委托是闭包，捕获了 ChatBot 和本实例（this）。插件被重载时 OnDestroy → Release，
        // 如果 ChatBot 活得比插件长（重载插件而不重启 Alife），旧委托就留在会话上：
        //   1) 新运行时的 AttachTransform 会因为字典里已有该 bot 而判定“已挂”，
        //      实际挂在上面的是**旧运行时**的委托 —— 插件覆盖从此指向被回收的实例；
        //   2) 旧运行时被这个闭包引用着，回收不掉 —— 每重载一次泄漏一份。
        //
        // 第三十二轮之前这里摘的是 LanguageModel.ContextTransform（别人给 OpenAI 插件加的口子），
        // 现在换成官方事件，摘除对象同步改成 ChatBot。
        try { DetachTransforms(); } catch (Exception ex) { ContextTrace.Write("detach transforms failed: " + ex.Message); }
        // 2026-10-02：这里以前要「把待批审批请求全部叫醒（视为拒绝）」——
        // 那套「await 用户弹窗」的机制已被整体删除（它会导致工具调用永久挂住对话），
        // 所以这段收尾也没有存在意义了。
        agentOps = null;
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
            // 2026-10-02：「agent-answer」（审批弹窗的回答）分支已删除 ——
            // 审批机制整体移除，前端也不再发这个报文。旧报文进来只有下面 else 的「忽略」日志。
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
        // 身份指纹：下标会变、内容不会。前端拿它当 framework 模块的配对键（比 #N 可靠）。
        // ⚠️ 必须**整批一起算**：同一个模块可以注入多条「功能说明」（Toolkit 按分层暴露就是
        // 每层一条），它们的模块名相同 —— 逐条各算各的会让指纹撞车，配对时
        // FirstOrDefault 永远命中第一条，后面几条卡片就串位/互相覆盖。
        // ComputeUniqueAnchorKeys 会对重复指纹按先后顺序追加 ~1 / ~2 消歧。
        var anchors = ContextPromptText.ComputeUniqueAnchorKeys(history
            .Select((message, i) => (
                message.Role == AuthorRole.System ? "live-system" : "live-history",
                message.Role.Label,
                message.Content,
                message.Role == AuthorRole.System && i == 0))
            .ToList());
        var index = 0;
        // system 消息自己的序号（0 = 角色设定）。**不能**用全局 index：
        // 那个是「遍历 ChatHistory 的计数器」，位置一漂标题里的名字就跟着漂（见 LiveSystemTitle）。
        var systemOrdinal = 0;
        foreach (var message in history)
        {
            var role = message.Role.Label;
            var isSystem = message.Role == AuthorRole.System;
            var attachments = ExtractAttachments(message, includeBinary: false);
            var text = message.Content ?? "";
            int level = 0;
            var title = isSystem
                ? LiveSystemTitle(text, systemOrdinal)
                // ⚠️ 非 system 消息（user / assistant）的标题**同样不带 #N**：
                // `#N` 早已不再是「数组下标」（见 LiveIndex 的注释），留在标题里只会诱导
                // 别人再去解析它。位置一律走 LiveOrder。
                // 角色名 + 正文摘要给用户看，比一个会漂的数字有用得多。
                : $"{role} · {ShortPreview(text)}";
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
            item.AnchorKey = anchors.TryGetValue(index, out var anchor) ? anchor : "";
            // 显示次序：给前端一个**整数**，免得它去解析标题里的 #N（见 ContextItem.LiveOrder 注释）。
            // 只用于排序，不参与身份配对。
            item.LiveOrder = index;
            if (isSystem) systemOrdinal++;
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
        // ⚠️ 用 LiveIndex，**不要**再 ExtractHistoryIndex(item.Title)：
        // 标题早已不带 #N（见 LiveSystemTitle 的注释），解析标题会永远返回 -1 →
        // 「无法定位 History.json 条目」，而且以前还会拿错位的 N 去改**别人**那一条。
        var index = LiveIndex(item);
        if (index < 0 || index >= arr.Count) throw new InvalidOperationException("无法定位 History.json 条目");
        arr[index]["Content"] = content;
        File.WriteAllText(path, arr.ToString(Formatting.Indented));
    }

    void DeleteHistoryItem(ContextItem item)
    {
        var path = GetHistoryPath(item.Owner);
        var arr = JArray.Parse(File.ReadAllText(path));
        // ⚠️ 同样是数据破坏高风险点：拿错位的下标会把**别的消息**删掉。必须用 LiveIndex。
        var index = LiveIndex(item);
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

    /// <summary>
    /// ⚠️ <b>历史遗留：从标题尾部抠 <c>#N</c>。</b>只在**标题里那个 N 确实是数组下标**的
    /// 场景才可以用 —— 也就是 <c>LiveOrder</c> 字段存在之前的**老报文**兜底。
    ///
    /// <para><b>不要再在新代码里用它取 ChatHistory / History.json 的下标</b>，
    /// 请用 <see cref="LiveIndex"/>。原因见那个方法的注释（这是一次真实的翻车）。</para>
    /// </summary>
    static int ExtractHistoryIndex(string title)
    {
        var match = Regex.Match(title, @"#(\d+)$");
        return match.Success ? int.Parse(match.Groups[1].Value) : -1;
    }

    /// <summary>
    /// 取一条实时条目在 <c>ChatBot.ChatHistory</c> / <c>History.json</c> 里的**真实数组下标**。
    ///
    /// <para><b>⚠️ 这是一次真实的翻车，别改回去。</b></para>
    ///
    /// <para>第五批修复把「实时 system 消息的标题序号」从「遍历 ChatHistory 的<b>全局下标</b>」
    /// 改成了「<b>system 消息自己的序号</b>」（即 <c>LiveSystemTitle(text, systemOrdinal)</c>）——
    /// 那个改动对<b>显示</b>是正确的（名字不该随位置漂），但它**悄悄弄坏了下标**：</para>
    ///
    /// <list type="number">
    /// <item>标题里 <c>#N</c> 的 N 从此是「第几条 system 消息」，**不再是数组下标**；</item>
    /// <item>而 <c>FillRuntimeSystemModuleContents</c> / <c>ApplyLiveContentEdit</c> /
    /// <c>DeleteHistoryItem</c> / <c>UpdateHistoryItem</c> / <c>ReadFullContent</c> 全都
    /// 还在 <c>ExtractHistoryIndex(item.Title)</c> 拿它当**下标**用。</item>
    /// </list>
    ///
    /// <para>两者只在「所有 system 消息恰好连续排在开头」时才相等。一旦角色设定之后插入了
    /// 消息（本插件自己注入的说明就是），就全部错位 —— 表现为：</para>
    /// <list type="bullet">
    /// <item>卡片**内容张冠李戴**（用错的 N 去取 ChatHistory，取到别人的正文）；</item>
    /// <item>同名模块**重复出现**（两条 system 消息取到同一段内容，看起来就是「重复」）；</item>
    /// <item><b>更严重</b>：编辑 / 删除会**改错、删错那条消息** —— 数据破坏。</item>
    /// </list>
    ///
    /// <para><b>现在的规则：位置只从 <c>LiveOrder</c> 取，标题里不再放 <c>#N</c>。</b>
    /// 身份（名字）与位置彻底分开。老报文没有 <c>LiveOrder</c>（默认 -1）时才退回解析标题。</para>
    /// </summary>
    static int LiveIndex(ContextItem item)
    {
        if (item.LiveOrder >= 0) return item.LiveOrder;
        return ExtractHistoryIndex(item.Title);   // 老报文兜底（那时 #N 确实是下标）
    }

    // 编辑「实时上下文 / 原始记忆流」条目 = 直接改写 ChatHistory 里那一条消息。
    // 这里以前失败是静默 return：用户点了保存、界面刷新后又变回旧内容，看起来就是
    // “原始记忆流不能修改”。现在改成抛出明确原因，让界面能说清为什么没写进去。
    //
    // ⚠️⚠️ 定位必须走**指纹**（AnchorKey），不能只信下标 —— 这是一次真实的翻车，
    // 起因就是「用错位的下标去改历史」把**别的插件**（VirtualWorldService）注入的
    // system 提示词改掉了：那条消息一旦不再以 `[功能说明(VirtualWorldService)]` 开头，
    // VirtualWorldService 重建 Interactor 时就 Find 不到它，于是**又插一条** →
    // 用户看到的就是「同一个模块出现两次」。见 AnchorKey 与 ResolveFrameworkIndex 的注释。
    void ApplyLiveContentEdit(ContextItem item, string content)
    {
        var activity = FindActivity(item.Owner);
        if (activity == null)
            throw new InvalidOperationException($"角色「{item.Owner}」当前未激活，无法改写实时上下文。请先激活该角色再编辑。");
        var index = item.SourceKey == "character-prompt"
            ? 0
            // ⚠️ 必须用 LiveIndex（LiveOrder），不能解析标题：标题已不带 #N。
            : LiveIndex(item);
        if (index < 0)
            throw new InvalidOperationException($"无法定位这条实时消息（没有可用的位置信息）。");
        var written = false;
        var mismatch = false;
        activity.ChatBot.EditChatHistory(thread =>
        {
            // ① 指纹校验：确认「第 index 条」确实是我们要改的那条。不是就**拒绝**，
            //    绝不改错 —— 宁可让用户刷新重试，也不能破坏别人的注入内容。
            if (index >= 0 && index < thread.ChatHistory.Count
                && !LiveMessageMatches(thread.ChatHistory, index, item))
            {
                mismatch = true;
                return;
            }
            if (index >= 0 && index < thread.ChatHistory.Count)
            {
                thread.ChatHistory[index].Content = content;
                written = true;
            }
        }, "ContextManager 编辑上下文");
        if (mismatch)
            throw new InvalidOperationException(
                "这条消息在对话历史里的位置已经变了（前后被增删/压缩过），为避免改错别的消息，本次编辑已取消。请刷新后重试。");
        if (!written)
            throw new InvalidOperationException(
                $"实时上下文当前只有 {activity.ChatBot.ChatHistory.Count} 条，序号 #{index} 已经不存在（对话可能被压缩或截断过）。请刷新后重试。");
    }

    void RemoveLiveMessage(ContextItem item)
    {
        var activity = FindActivity(item.Owner);
        if (activity == null) return;
        // ⚠️ 同 ApplyLiveContentEdit：这是**删除**路径，用错下标会删掉不该删的那条
        // （删掉 VirtualWorldService 的提示词不会立刻有症状，但会诱发它重复注入）。
        var index = LiveIndex(item);
        if (index <= 0 || index >= activity.ChatBot.ChatHistory.Count) return;
        if (!LiveMessageMatches(activity.ChatBot.ChatHistory, index, item)) return;
        activity.ChatBot.EditChatHistory(thread =>
        {
            if (index < thread.ChatHistory.Count && LiveMessageMatches(thread.ChatHistory, index, item))
                thread.ChatHistory.RemoveAt(index);
        }, "ContextManager 删除上下文");
    }

    /// <summary>
    /// 校验历史里第 <paramref name="index"/> 条消息，就是界面上的 <paramref name="item"/>。
    ///
    /// <para><b>判定顺序（越靠后越松，只有全都不成立才判「不是」）：</b></para>
    /// <list type="number">
    /// <item>有条目指纹就用指纹严格比对（<see cref="ContextPromptText.ComputeAnchorKey"/>，
    /// 与装配计划里的配对用的是同一套算法）；</item>
    /// <item>没有指纹（老报文 / 磁盘条目）时，退化成「角色一致 + 正文一致」——
    /// 只做**相等**判断，绝不做「包含 / 前缀」这类模糊匹配。</item>
    /// </list>
    ///
    /// <para>为什么不干脆只比正文：正文本身就是用户要改的东西，改完就不相等了；
    /// 这里的调用发生**在写入之前**，比的是「写之前那条是不是它」，所以等值比对是安全的。</para>
    /// </summary>
    bool LiveMessageMatches(IReadOnlyList<ChatMessageContent> history, int index, ContextItem item)
    {
        var message = history[index];
        if (item.AnchorKey.Length > 0)
        {
            return ContextPromptText.HistoryAnchors(history)[index] == item.AnchorKey;
        }
        // 没有指纹：退化成「正文比对」。⚠️ item.Content 可能只是**预览**（报文超预算时被
        // PreviewLimit 截断，见 BuildLiveContext 里的赋值），所以**不能要求完全相等** ——
        // 改成「二者互为首缀」：任一为空则判否（无信息量，宁可不动）。
        var actual = message.Content ?? "";
        var shown = item.Content ?? "";
        if (shown.Length == 0) return false;
        return actual.StartsWith(shown, StringComparison.Ordinal)
            || shown.StartsWith(actual, StringComparison.Ordinal);
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

    /// <summary>
    /// 用 <c>ChatHistory</c> 里的**全文**替换 live-system 条目的预览正文。
    ///
    /// <para><b>⚠️ 这里以前是截图里那些怪现象的直接来源</b>：它用
    /// <c>ExtractHistoryIndex(item.Title)</c> 从标题抠 <c>#N</c> 当 ChatHistory 的**下标**。
    /// 而标题里的 N 早已改成「第几条 system 消息」（见 <see cref="LiveSystemTitle"/>），
    /// 两者只在「所有 system 消息恰好连续排在开头」时才相等。
    /// 角色设定之后一旦插入了消息（本插件自己注入的说明就是），就整体错位 —— 表现为：</para>
    /// <list type="bullet">
    /// <item>卡片**正文张冠李戴**：用错的 N 取到**别人**的正文；</item>
    /// <item>同名模块**看起来重复**：两条 system 消息的 N 都落在同一段正文上，
    /// 于是两张卡片显示一模一样的内容（用户报的 VirtualWorldService #4 / #5 就是这个）。</item>
    /// </list>
    ///
    /// <para>改用 <see cref="LiveIndex"/>（读 <c>LiveOrder</c>，真实下标）后两者一致。</para>
    /// </summary>
    void FillRuntimeSystemModuleContents(ChatActivity activity, string owner, List<ContextItem> items)
    {
        foreach (var item in items.Where(i => i.Owner == owner && i.SourceKey == "live-system"))
        {
            var index = LiveIndex(item);
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
            var activityForPrune = FindActivity(owner) ?? throw new InvalidOperationException("角色未激活，不能清理实时对话");
            var historyForPrune = activityForPrune.ChatBot.ChatHistory;
            // 判断「哪些下标允许删」时，按模块自己的**真实当前位置**判定（指纹优先），
            // 而不是 plan 里那份可能已经漂移的 TargetIndex —— 否则历史一变就会放行删错消息。
            var excluded = plan.Modules
                .Where(module => module.Source == "framework" && module.Group == "chat" && !module.Enabled)
                .Select(module => ResolveFrameworkIndex(module, historyForPrune))
                .Where(index => index >= 0).ToHashSet();
            if (requested.Any(index => !excluded.Contains(index)))
                throw new InvalidOperationException("只能删除已取消参与装配的对话消息");
            lock (stateLock)
            {
                var activity = activityForPrune;
                var removable = requested.Where(i => i > 0 && i < activity.ChatBot.ChatHistory.Count && activity.ChatBot.ChatHistory[i].Role != AuthorRole.System).ToList();
                if (removable.Count == 0) throw new InvalidOperationException("没有可删除的 user/assistant 对话；系统消息不会被清理");
                activity.ChatBot.EditChatHistory(thread =>
                {
                    foreach (var index in removable) if (index < thread.ChatHistory.Count) thread.ChatHistory.RemoveAt(index);
                }, "ContextManager 批量清理未参与装配的对话");

                var service = GetPlanService();
                // 删掉对应的 framework 模块（按指纹识别，不用可能已漂移的下标），
                // 剩下的模块下标统一重算一遍 —— 重算同样走指纹，重算完就落盘，
                // 这样 plan 里的 TargetIndex 永远是「当前历史的真实位置」。
                plan.Modules.RemoveAll(module => module.Source == "framework"
                    && ResolveFrameworkIndex(module, activity.ChatBot.ChatHistory) is var at
                    && at >= 0 && removable.Contains(at));
                foreach (var module in plan.Modules.Where(module => module.Source == "framework"))
                {
                    var at = ResolveFrameworkIndex(module, activity.ChatBot.ChatHistory);
                    if (at >= 0) module.TargetIndex = at;
                }
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

    /// <summary>
    /// 实时 system 消息的标题。<b>标题里只有名字，没有任何编号。</b>
    ///
    /// <para><b>⚠️⚠️ 这里是本项目最贵的一个坑，前后修了三批才彻底对。</b></para>
    ///
    /// <list type="number">
    /// <item><b>第三批</b>：`index` 是**遍历整个 ChatHistory 的全局下标**，拼成 `"{名字} #{index}"`。
    /// 名字从消息自身抠（对），但 `#N` 是**位置**，会随「有没有别的消息插进来」漂移 ——
    /// 于是同一个 `#3` 一会儿是 A 模块、一会儿是 B 模块，用户看到「模块名和内容对不上」。</item>
    /// <item><b>第五批</b>：把 `#N` 改成「**第几条 system 消息**」（`systemOrdinal`）。
    /// 对**显示**而言这是对的（名字、序号都不再随位置漂），但它**悄悄弄坏了真正需要下标的地方**：
    /// `FillRuntimeSystemModuleContents` / `ApplyLiveContentEdit` / `DeleteHistoryItem` /
    /// `UpdateHistoryItem` / `ReadFullContent` 都还在用 `ExtractHistoryIndex(item.Title)`
    /// 把标题里的 `#N` 当**数组下标**用 —— 于是卡片内容张冠李戴、同名模块重复显示，
    /// 编辑和删除更会**改错、删错那条消息**。</item>
    /// <item><b>本批（第六批）</b>：认清根本问题 —— **`#N` 这个字段同时被当成了「名字的一部分」
    /// 和「数组下标」，两种语义不可能共用一个数字**。所以：</item>
    /// </list>
    ///
    /// <para><b>现在的规则（不许再改回去）</b>：</para>
    /// <list type="bullet">
    /// <item><b>标题 = 纯名字</b>（`VirtualWorldService` 或 `角色设定`），不含任何 `#N`。</item>
    /// <item><b>位置 = <see cref="ContextItem.LiveOrder"/></b>（整数），只用于排序与取下标，
    /// 由 <see cref="LiveIndex"/> 读取，<b>绝不从标题里解析</b>。</item>
    /// <item>界面上要显示位置时，前端用 `liveOrder` 自己拼「实际位置 #N」，与后端解耦。</item>
    /// </list>
    /// </summary>
    static string LiveSystemTitle(string text, int systemOrdinal)
    {
        if (systemOrdinal == 0) return "角色设定";
        var name = ContextPromptText.LiveSystemName(text);
        // 名字抠不出来（用户自己加的 system 模块）→ 给一个**稳定**的兜底名。
        // 不用序号：序号会随「用户增删别的 system 模块」而变，名字不该跟着变。
        return name.Length > 0 ? name : "系统模块";
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
            // offline 条目没有 LiveOrder（非实时），LiveIndex 会退回解析标题 —— 行为不变。
            var wanted = LiveIndex(item);
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
                // ⚠️「读取全文」是**不可逆**的（前端可能写回），下标必须准。用 LiveIndex。
                var idx = LiveIndex(item);
                if (idx >= 0 && idx < activity.ChatBot.ChatHistory.Count)
                    return activity.ChatBot.ChatHistory[idx].Content ?? "";
            }
        }
        if (item.SourceKey == "live-history")
        {
            var activity = FindActivity(item.Owner);
            if (activity != null)
            {
                var idx = LiveIndex(item);
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
                    // 离线历史是从磁盘按顺序重建的，下标天然稳定；但仍然记一份指纹，
                    // 好让「同一条对话」在角色激活后（live）与未激活时（offline）能被认出来是同一件事。
                    AnchorKey = ContextPromptText.ComputeAnchorKey("live-history", role, text),
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
        // ⚠️ 以前这里硬编码了「live-system 一定是第 0 条」（`? 0 : …`）——
        // 硬编码「live-system 一定是第 0 条」。这在**有多个 system 模块**时是错的
        // （VirtualWorldService 这类会取到角色设定那条的附件）。统一走 LiveIndex。
        var idx = LiveIndex(item);
        if (idx < 0 || idx >= activity.ChatBot.ChatHistory.Count) return;
        var message = activity.ChatBot.ChatHistory[idx];
        item.Attachments = ExtractAttachments(message, includeBinary: true);
        item.EstimatedTokens = EstimateTokens(message.Content ?? "") + item.Attachments.Sum(a => 120);
    }

    /// <summary>
    /// ⚠️ <b>Deprecated：不要再用。</b>它与 <see cref="ExtractHistoryIndex"/> 是同一份正则的
    /// 第二份拷贝 —— 两份实现迟早漂移，而且它们都已被 <see cref="LiveIndex"/> 取代。
    /// 保留只为兼容老报文（<c>LiveOrder</c> 缺失时）。
    /// </summary>
    int MessageIndexFromTitle(string title) => ExtractHistoryIndex(title ?? "");

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

    void DeleteTempOverride(string owner)
    {
        try
        {
            var file = GetOverrideFile(owner);
            if (File.Exists(file)) File.Delete(file);
        }
        catch (Exception ex) { ContextTrace.Write("delete temp override failed: " + ex.Message); }
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
        string? dir = null;
        try { dir = ResolveCharacterDirectory(GetCharacterRequired(owner), FindStorageRoot()); }
        catch (Exception ex) { ContextTrace.Write($"preset sources(character) failed owner={owner}: {ex.Message}"); }

        var worldBookPath = string.IsNullOrWhiteSpace(dir) ? null : Path.Combine(dir, "WorldBook.json");
        var cardPath = string.IsNullOrWhiteSpace(dir) ? null : Path.Combine(dir, "TavernCard.json");
        // 酒馆预设：快照带上**目录里存在的全部预设文件**。
        // （以前只带 Config.ActivePreset 那一个；那个字段已随死路径删除，
        //   而且「只带当前选中的一份」本身也不对 —— 换角色导入时对方可能根本没有那个名字。）
        var presetPaths = new List<string>();
        try
        {
            var presetDir = GetPresetDirectory();
            if (Directory.Exists(presetDir))
                presetPaths.AddRange(Directory.GetFiles(presetDir, "*.json").OrderBy(f => f, StringComparer.Ordinal));
        }
        catch (Exception ex) { ContextTrace.Write($"preset sources(presets) failed owner={owner}: {ex.Message}"); }

        var fingerprints = new List<string>();
        foreach (var path in new[] { worldBookPath, cardPath }.Concat(presetPaths))
            if (!string.IsNullOrWhiteSpace(path)) fingerprints.Add(Fingerprint(path!));
        fingerprints.Sort(StringComparer.Ordinal);

        if (previous != null && previous.Fingerprints.Count == fingerprints.Count
            && previous.Fingerprints.SequenceEqual(fingerprints, StringComparer.Ordinal))
            return previous;

        var sources = new CharacterPresetSources
        {
            CapturedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            Fingerprints = fingerprints
        };
        if (worldBookPath != null) sources.WorldBook = ReadJsonObjectOrNull(worldBookPath);
        if (cardPath != null) sources.TavernCard = ReadJsonObjectOrNull(cardPath);
        // 单份预设仍走 Preset 字段（保持与旧快照格式兼容），多余的在 RestorePresetSources 按文件写回。
        if (presetPaths.Count > 0) sources.Preset = ReadJsonObjectOrNull(presetPaths[0]);
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
        // 酒馆预设：只在**目标目录里还没有同名预设文件**时才写回 —— 用户本机已有的预设不该被快照覆盖。
        try
        {
            if (!string.IsNullOrWhiteSpace(sources.ActivePreset) && sources.Preset != null)
            {
                var target = Path.Combine(GetPresetDirectory(), SafeFileName(sources.ActivePreset) + ".json");
                if (!File.Exists(target))
                {
                    AtomicWrite(target, sources.Preset.ToString(Formatting.Indented));
                    notes.Add($"酒馆预设「{sources.ActivePreset}」");
                    ListPresets();
                }
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

    internal List<Character> GetCharacters()
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





