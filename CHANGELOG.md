# 上下文管理器 · 开发日志

> 这份文件是按轮次累积的开发记录（每一轮解决了什么、为什么这么改、踩了哪些坑），
> 面向维护者。**使用者请看 [README.md](README.md)。**

当前版本：`2026-10-02ah`

## 第三十三轮（删除审批弹窗 —— 「等用户点弹窗」的准入机制会把整个对话卡死）

用户反馈：
> 「有时候执行完，执行 AgentContextAdd 函数丨分析对话，会执行成功，但是在之后，
> 我想继续对话，结果发现结果**被一直这个：执行 AgentContextAdd 函数丨分析对话；给占用住了，
> 发言发不出去了**」
> 「实际上我没有看到过那个**审批弹窗**」

### 根因：准入判定 `await` 一个永远等不到的回答

`CheckAgentAccessAsync` 在「改别人 + 对方没启用插件覆盖」这条分支里：

```
await AskAgentAsync(...)                    ← 卡在这里
  → SendWindow("agent-ask", ...)            ← 发弹窗请求给渲染进程
  → await tcs.Task                          ← 等用户点「同意 / 拒绝」
```

**弹窗从来没有真正显示出来**（用户明确说没见过）⇒ 用户不可能回答 ⇒
那个 `TaskCompletionSource` 永远不会 `SetResult` ⇒ 这次工具调用**永久挂起**。
`XmlHandler` 的 Invoker 在 `await` 这个工具调用，工具调用不返回，框架的对话轮次就完不成 ——
于是**整个会话的发送通道被这次调用占死**，UI 上一直显示「执行 AgentContextAdd 函数丨分析对话」。

⚠️ 那段 `while (true) { await Task.WhenAny(tcs.Task, Task.Delay(5000)) }` 看起来像超时保护，
其实是个**假保险**：它只在「插件窗口被关掉」时才 `break`。窗口一直开着但弹窗没渲染出来，
这个循环就永远转下去 —— **有轮询、没有出路**。

### 修法：整体删除，不是修修补补

审批机制的设计前提是「用户会看到弹窗并回答」。**前提不成立时，它就是一个必然卡死的陷阱**，
而卡住的代价（整个对话不能用）远大于它想防住的风险。所以彻底移除：

| 位置 | 改动 |
|---|---|
| `ContextAgentGuard.cs` | `CheckAgentAccessAsync`（async + await 弹窗）→ **`string? CheckAgentAccess(...)` 同步方法**，立即返回 |
| `ContextAgentGuard.cs` | 删除 `AskAgentAsync` / `AnswerAgentAsk` / `pendingAgentAsks` / `agentAskGate` / `AgentDecision` |
| `ContextAgentGuard.cs` | 插件覆盖未启用时：**改自己、改别人都直接自动启用**（插件覆盖只在请求前重排，不写任何角色文件，关掉插件即复原） |
| `ContextAgentApi.cs` | 8 个调用点：`await runtime.CheckAgentAccessAsync(...)` → `runtime.CheckAgentAccess(...)` |
| `ContextManagerRuntime.cs` | 删除 `agent-answer` IPC 分支；删除 `Release()` 里的「叫醒待批审批」收尾 |
| `app.js` | 删除 `showAgentAsk` / `answerAgentAsk` / `AGENT_RISK_NOTE` / `agent-ask` 弹窗分支（收到旧报文只记 trace） |
| `agent-ops-tests` | 10.4~10.20、12.1~12.9 全部**反过来钉**「不存在任何等用户输入的通道」；14.4 钉「后端不再分派 agent-answer」 |
| `smoke-test.cjs` | 新增「前端无审批弹窗」源码断言 |
| `mutate-x.py` | 新增变异 `approval`（把准入退回 await 弹窗）→ 被抓 |

### 保留 vs 删除

**保留**：配置页两个开关（允许改自己 / 允许改别人）—— 那是用户对信任的一次性表达，同步生效。
**保留**：世界书弹窗（`card-worldbook-ask`）—— 那个弹窗**是真的会出现的**，且它的 `await`
有真实出口，不属于这类陷阱。
**删除**：所有「弹窗出现才安全」的准入路径。

### 验证矩阵（全绿）

- 编译校验：通过，20 个 `.cs`，290816 字节（比上轮少 7168 —— 删掉的就是审批机制）。
- `agent-ops-tests`：**392 项全过**。
- 冒烟测试：**48 项全过**（新增「审批弹窗已删除」）。
- 文档渲染：**8 项全过**。
- 变异 `approval`（准入退回 await 弹窗）→ **被抓**：
  `FAIL: 10.4 不存在「等用户回答」的准入通道（它会导致工具调用永久挂住）`。
- 审计：`Alife.Function.Language.OpenAI.dll` 非本插件产出；我方只写 `Marisa.ContextManager` 自己的文件。

> **铁律：任何「await 用户输入」的准入机制，只要「弹窗真的会出现」这个前提不成立，
> 就等价于一个必然卡死的陷阱。**
> 「有轮询」不等于「有出路」—— 轮询的 `break` 条件如果依赖另一个**同样可能不发生**的事件
> （窗口关闭），那它只是把「永久挂住」包装得好看了一点。
> **准入判定必须同步、立即、可预测。**

## 第三十二轮（斩断对 OpenAI 插件私有接口 `ContextTransform` 的强依赖）

用户反馈：
> 「[来自系统的杂项消息推送][消息来源(ContextManagerModule)]应用失败：当前语言模型没有
> ContextTransform 接口，请先重载已更新的 OpenAI 语言模型插件。」
> 「**不是你该改什么 OpenAI 语言模型插件啊，如果强依附别人的插件，很麻烦的**」
> 「我再修改让 AI 修改第三方角色的预设给的提示词」
> 「这样子，我先回退一下 OpenAI 语言插件」
> 「好了，**你现在完全只在插件里操作**」

### 根因：`ContextTransform` 根本不是官方 API，是别人给 OpenAI 插件加的私有口子

查证（全部实证）：

1. **官方源码全库搜 `ContextTransform` 零命中**（`Alife-master`）。
2. 官方 `ILanguageModel`（`Alife.Framework/Models/ILanguageModel.cs`）**只有**
   `ChatStreamingAsync(...)` 一个方法，**没有任何请求前钩子**。
3. 那个属性是**别人**给 OpenAI 插件源码硬加的（本仓库里 `edit.py` / `permanent-fix.py`
   就是当年改它的遗迹）。插件用它做「请求前重排」，等于**强依附一个不受官方保证的私有口子**：
   对方一升级/回退/换模型，整条链路当场死掉，而且报错还会把责任推给用户（「请重载插件」）。

用户当场质疑得对：**插件不该去改别的插件，也不该依赖别人插件里的私有接口。**

### 修法：改用官方公开挂载点 `ChatBot.ChatSent`（零第三方依赖）

`ChatBot.ChatAsync` 的时序（`Alife.Framework/Models/ChatBot.cs`）：

```
127  EditChatHistoryAsync(装载用户消息)      ← 用户消息已进历史
136  ChatSent?.Invoke(message.Content)        ← 我们在这里重排
149  EditChatHistoryAsync(发真请求)           ← 语言模型读到的是重排后的历史
```

- **`ChatSent` 是公开事件**（`ChatBot.cs:36` `public event Action<string>? ChatSent`）——
  官方 API，任何语言模型都有，不存在「接口缺失」。
- **不死锁**：`ChatSent` 触发时第 127 行那次 `EditChatHistoryAsync` 已释放
  `chatHistorySemaphore`，所以回调里同步调 `ChatBot.EditChatHistory(...)`
  （`ChatBot.cs:75`，public 同步方法）是安全的。
- **粒度**：`bot.ChatHistory` 是对外**快照**（`IReadOnlyList<ChatMessageContent>`，第 54 行），
  而 `ContextCompiler.Compile` 要 `ChatHistory` —— 先按序拷一份喂编译，
  再在 `EditChatHistory` 里「整体替换」（`Clear()` 后按编译结果重填）。

### 改动清单（全部在 `Marisa.ContextManager` 插件内）

| 位置 | 改动 |
|---|---|
| `ContextAssemblyRuntime.cs` 字段 | `Dictionary<object, Func<ChatHistory,ChatHistory>> transforms` → `Dictionary<ChatBot, Action<string>> chatSentSubscriptions`（+ `chatSentGate` 独立锁） |
| `AttachTransform` | 反射写 `LanguageModel.ContextTransform` → `bot.ChatSent += handler`；闭包只捕获 `bot` + `owner`（字符串），不捕获 `activity` |
| `ReorderBeforeSend`（新增） | 在 `ChatSent` 里：读计划 → 拷历史 → `ContextCompiler.Compile` → `bot.EditChatHistory` 整体替换；全程 try/catch，绝不外抛 |
| `TryAutoAttach` | 返回 `AttachTransform(activity)`，不再看 `LanguageModel` |
| `OnActivityDeactivated` / `DetachTransform`（新增）/ `DetachTransforms` | 摘除对象从 `LanguageModel` 换成 `ChatBot` |
| `ApplyPlanCore` | **删掉**「挂不上就抛『没有 ContextTransform 接口』」那段；挂不上只记日志 |
| `ContextManagerRuntime.cs` | `Release()` 注释同步；两个文件删掉不再需要的 `using System.Reflection;` |
| `ContextCompiler.cs` | 注释里的 `ContextTransform` 改成 `ChatSent 请求前重排` |
| `smoke-test.cjs` | 新增两条断言：**不许再反射 `ContextTransform`** / **必须 `bot.ChatSent += handler`** |

### 为什么「AI 改第三方角色只改计划文件、不动角色文件」现在成立

用户明确要求 AI 改别人时**不要碰对方的 `index.json`**。这就要求改完必须靠
「请求前重排」生效，而这正是本轮把 `ContextTransform` 换成 `ChatSent` 的原因：
官方事件 + `EditChatHistory` 是纯运行时重排，**不落任何角色文件**，关掉插件即复原。

### 验证矩阵（全绿）

- 编译校验：通过，20 个 `.cs`，297984 字节（与上轮一致）。
- `agent-ops-tests`：**396 项全过**。
- 冒烟测试：**48 项全过**（新增 2 条 ContextTransform 禁令）。
- 文档渲染：**8 项全过**。
- 变异 `chatsent`（把订阅退回反射 `ContextTransform`）→ **被抓**：
  `FAIL: ContextAssemblyRuntime 不许再反射 ContextTransform`。
- 审计：`Alife.Function.Language.OpenAI.dll` 非本插件产出（用户自行回退后由 Alife 重编译，
  源码 9673 字节、`ContextTransform`/`ContextAssembly`/`ContextManager` 零命中）；
  我方只写了 `Storage\Plugins\Marisa.ContextManager\*` 与 `Storage\ContextManager\*`。

> **铁律：插件只能依赖官方公开 API。「别人插件里的私有口子」看着能用，
> 但对方一升级就断，还会把断的责任推给用户。**

## 第三十一轮（「首次改别人成功，之后连给自己加模块都被拦」—— 配置被多角色互相冲掉）

用户反馈：
> 「我让第三方其他 AI 角色修改 AI 角色的，结果**只有 AI 使用首次修改其他角色指令成功了**，
> 后面都提示：『用户没有开启「允许角色修改其他角色的上下文」，所以这次保存快照被拒绝了』；
> 然后我让 AI 再**给自己新增调用模块，结果也被拦了**，感觉是触发什么 bug 了？**我权限本身都是开着的啊**」

### 根因：全局单例上放了一份「每个角色各不相同」的配置

一条链、三个事实叠出来：

1. **配置是「按角色」存的**。框架 `ChatActivity` 在激活模块时，
   按 `character.StorageKey` 逐个注入 `Configuration`（源码 `Alife.Framework/Models/ChatActivity.cs:80-83`，
   注入发生在 `AwakeAsync` 之前）。落盘位置：
   `Storage/Character/<角色>/Configuration/Marisa.ContextManager.ContextManagerModule.json`。
2. **本项目有 5 个角色装了本插件**：`momo`（配置文件里两个开关都是 `true`）、
   `伊卡洛斯 / 小梦 / 星野雨 / 酒狐`（**没有配置文件** → 框架 `Activator.CreateInstance`
   造一个默认实例 → 两个开关都是 `false`）。
3. **`ContextManagerRuntime` 是全局单例**（`GetOrCreate` 里 `current ??= new ...`），
   而 `Config` 只是这个单例上的**一个**属性。老实现里 `ContextManagerModule.OnUpdate()`
   **每帧**都调 `Runtime.ApplyConfig(Configuration)` —— 把**本角色**的配置写进这个共享槽位。

**合起来**：`Config` 的值 = 「最后一个跑 `OnUpdate` 的角色的设置」。
momo 开了权限、改别人第一次成功；此后只要 momo 之外的任一角色也在跑，
`Config` 就被冲成全 `false` —— momo 的权限被**无声撤销**。
这**完整解释了两个现象**：

- **「首次成功、之后全拒」**：第一次时 `Config` 恰好还是 momo 的（true, true）；
  之后被别的角色覆盖成（false, false）。
- **「连给自己加模块也被拦」**：整份 `Config` 被冲成 false，`AllowModifySelf` 也变 false，
  于是给自己加模块同样被那条最外层的开关判断挡下（并且因为 `IsSelf` 为 false 之外的分支也走不同文案，
  用户看到的是「其他角色」那段话 —— 正是他贴的原话）。

### 修法：把「一个共享槽位」换成「按 owner 查」（不动任何既有语义）

`ContextManagerRuntime.cs`：

```csharp
readonly Dictionary<string, ContextManagerConfig> ownerConfigs = new(StringComparer.OrdinalIgnoreCase);
readonly object ownerConfigGate = new();   // 独立锁：它每帧都写，且不参与任何 IPC 构建

public void  SetOwnerConfig(string? owner, ContextManagerConfig? c) { ... ownerConfigs[owner] = c; Config = c; }
public ContextManagerConfig ConfigFor(string? owner)               { ... 命中 owner 的就用它，否则回退兜底 Config }
```

- **独立锁**（不与 `stateLock` 共用）：配置字典每帧都会写，且不参与任何 IPC 报文构建，
  共用锁只会在「锁内构建快照」时互相蹭，没有任何好处。
- `Config` 保留为**兜底值**（owner 名对不上时仍有可用默认），`ApplyConfig` 保留作兼容，但已无调用方。

三个读取点全部改掉：

| 位置 | 老写法 | 新写法 |
|---|---|---|
| `ContextAgentGuard.CheckAgentAccessAsync`（准入闸门） | `Config.AllowModifySelf` / `Config.AllowModifyOthers` | `ConfigFor(request.SelfOwner)` —— **必须是「发起者自己」的配置** |
| `ContextAgentApi.BuildPrompt`（能力说明注入） | `runtime.Config.AllowModify*` | `runtime.ConfigFor(character.Name)` —— 否则「AI 忽而知道自己能改、忽而又不知道」 |
| `ContextManagerModule.OnAwake` / `OnUpdate` | `Runtime.ApplyConfig(Configuration)` | `Runtime.SetOwnerConfig(SafeName(), Configuration)` |

> `SafeName()`（= `Character.Name`）与框架注入配置用的 `character.StorageKey` 尾部段一致
> （`Character\momo` ↔ `momo`），所以按角色名登记能精确对上；且 `Character` 在 `OnAwake` **之前**
> 就已由框架赋值（`ChatBehaviour.AwakeAsync` 第 37 行 vs 第 43 行），登记时拿得到真名。

### 验证

| 工具 | 结果 |
|---|---|
| `agent-ops-tests` | **All 396 passed**（新增 10c.1~10c.10 共 10 条「配置按角色隔离」源码断言） |
| `compile-verify`（部署目录 20 .cs） | **20 .cs / 383 程序集 / 297984 字节** |
| `smoke-test.cjs` | 48 项全 PASS |
| `agent-doc-render` | PASS |
| `audit.py` | 硬约束 DLL sha256 未变 |
| 变异 `cfgshare`（把 `ConfigFor(SelfOwner)` 退回 `Config`） | **抓住** → `FAIL: 10c.5 准入判定按发起者自己的配置` |

### 本轮的坑与教训

1. **全局单例上不要放「每个实例各不相同」的可变状态。** 配置天然是 per-owner 的；
   把它摊到共享单例上，就等于让所有角色共用一个开关 —— 而且这种错误**不会立刻报错**，
   只在「恰好有另一个实例在跑」时才发作，表现为**间歇性、看起来像玄学**的权限失效。
2. **「首次成功、之后失败」是「共享状态被覆盖」的经典指纹。** 见到这个形状，
   第一反应就该去找「有没有一处共享槽位被别的实例按帧/按事件重写」。
3. **断言分层**：`ContextAgentGuard` / `ContextAgentApi` / `ContextManagerModule` / `ContextManagerRuntime`
   都不在 `agent-ops-tests` 的编译范围（它们依赖 Alife 框架）。所以本轮的修复用
   **源码级静态断言**（`ReadFile("...").Contains/Regex`）锁定 —— 这也是 `RefreshRegression`
   已经用过的套路：**测不到行为，就测「不该出现的老写法」与「必须出现的新写法」**。

当前版本：`2026-10-01ae`

## 第三十轮（AI 改了上下文，界面为什么不刷新？—— 补上缺掉的通知链）

用户反馈：
> 「AI 可以新增/修改的上下文模块我在管理界面里没有**实时刷新显示**，且那个**新增模块根本没有显示**，
> 但我在**预览界面确实能看见**那个模块确实存在」

一句话结论：**模块真的写进去了，但没有任何人告诉界面这件事。**
而且新增模块的来源是 `agent`，它**不在前端的来源白名单里** —— 所以哪怕计划推过来了也画不出卡片。

### 证据（先查现场，不猜）

`Plans/momo.json` 里确实躺着 AI 加的模块，说明写盘成功：

```
 4 | agent      | en=True  | system | len=29    | '测试模块A'
 5 | agent      | en=True  | system | len=20    | '测试模块B'
   UpdatedAt = 2026-10-01 20:47:33
```

那为什么界面看不到？两条独立的原因，**叠在一起**：

### 1. 【真实缺陷】写计划的层发不出通知（5 条写路径全部漏了）

- `ContextAgentOps` 是**纯逻辑层**（类注释原话：「不碰 IPC」），构造里只有 `planService`。
  它改完计划只 `SavePlan`，**拿不到 runtime、发不出 IPC**。
- `ContextAgentApi` 的写函数（`AddModule` / `UpdateModule` / `DeleteModule` / `ImportModules` /
  `LoadPreset`）**共 5 条写路径，全部只落盘、零通知**。
- 界面这一侧（`plan-state` / `state`）只在「用户自己保存 / 应用 / 历史被编辑」时推送。

⇒ 角色改完，磁盘变了，用户盯着的窗口**纹丝不动**，必须关掉重开才看得到。

### 2. 【真实缺陷】`agent` 不在前端来源白名单里

两个装配布局函数都拿 `planSources` 当**来源白名单**：

```js
const sources = assemblyFilter.source === 'all' ? planSources : planSources.filter(…)
const list = all.filter(m => (m.source || 'custom') === src);
```

而 `planSources` 里**没有 `agent`** ⇒ AI 新增的模块**任何布局下都不画**。
偏偏「预览」是后端 `ContextCompiler.Compile` 直接渲染的、**不走这份白名单** ——
这就精确对上了用户的描述：**预览有、界面没有**。

### 修法

**后端**（通知链）：
1. `ContextAgentOps` 新增第 5 个构造参数 `Action<string, ContextPlan>? onPlanChanged`，**由 Runtime 注入**；
   新增唯一落盘出口 `SaveAndNotify`：**先存盘、再通知**，通知抛异常只记日志。5 条写路径全部改走它。
2. `ContextManagerRuntime` 新增 `NotifyPlanChangedFromAgent(owner, plan)`：读计划 → `SendPlanUpdated` → `QueueInitialState`。
3. `ContextAssemblyRuntime` 新增 `SendPlanUpdated`：报文类型用 **`plan-updated`**，复用同一套 `ShrinkPlanForWire` 瘦身。

**前端**（能看见）：
4. `planSources` **补上 `["agent","AI 新增"]`** —— 这条单独就能让新模块显示出来。
5. 新增 `plan-updated` 分支：没在编辑就直接换上新计划；**正在编辑就保留草稿**，
   弹一条 `#externalPlanNotice` 提示条（「载入最新」/「保留我的」），
   并**作废旧的已读全文缓存**（AI 可能改写了正文）。
6. `readPlanFields` **改为「确实变了才置脏」**，且正文比对**先规范化换行**。
   原实现第一行就 `planDirty = true`，后果不只是多脏一次：用户点一下模块**看一眼**（什么都没改）
   就把自己标成「有未保存修改」，于是 AI 推来的更新只能变成提示条 —— 看到的还是「没同步」。
   另外 `<textarea>` 会把 `\r\n` 规范化成 `\n`，不规范化换行的话，一条含 CRLF 的正文**一打开就被判成改过了**。

### 三条约定（被 `agent-ops-tests/RefreshRegression.cs` 逐条锁定）

1. **必须在落盘之后调** —— 通知方要读新计划，先通知会读到旧内容；
2. **只在真正改动了才调** —— 读操作（outline / read / preview）与失败的写一律不调；
3. **通知失败绝不能影响那次写操作的结果** —— 计划已经落盘了，那是真东西；
   用户在没开窗口时操作，通知里会拿不到窗口，不能因此把「已保存」报成失败。

### 变异测试

`tmp/mutate-x.py` 新增两个模式，都验证过**能抓住**：
- `notify`：把 `SaveAndNotify` 退回「只存盘不通知」→ `FAIL: refresh: AI add notifies with agent source`；
- `agent`：把 `agent` 从白名单删掉 → 冒烟 `FAIL: agent source must be visible`。

### 本轮踩到的坑

- ⚠️ **`restore` 会用旧备份静默抹掉新修复**。跑变异前**必须先把 `tmp/*.PRISTINE.*` 刷新一遍**，
  否则第一次 `restore()` 就把本轮改动全部回滚。
- ⚠️ **`dotnet run --no-incremental` 之外还要清 `obj/` `bin/`**：有一次断言假失败，
  清掉缓存才恢复正常。
- ⚠️ `smoke-test.cjs` 的 strict-mode：`#externalPlanNotice button` 期望唯一匹配，
  所以提示条里**只能有一个 `<button>`**（「保留我的」做成链接样式）。

### 验证矩阵

| 验证 | 结果 |
|---|---|
| 编译校验（Alife 自己的 Roslyn） | 20 个 .cs / 383 程序集 / 297472 字节 |
| 源码级断言 | **All 386 passed** |
| 函数文档渲染 | 8 PASS / 0 FAIL |
| 前端冒烟 | 48 项 PASS |
| 零污染审计 | 硬约束 DLL sha256 未变 |
| 变异测试 | `notify` ✅ 抓住 / `agent` ✅ 抓住 |

构建号：`cm-app-2026-10-01-ad` → **`cm-app-2026-10-01-ae`**（四处同步：
`ContextManagerRuntime.AppBuild` / `app.js` 的 `APP_BUILD` / `index.html` 的两处 `?v=` / `smoke-test.cjs` 的 `BUILD`）。

---

## 第十七轮（长时间运行审计：内存 / 显存 / 插件覆盖长跑）

用户问：「检查一下这个插件如果长时间运行，比如在插件覆盖，会出现问题吗？会不会挤压显存？
内存占用怎么样？」——逐项审计后：**会随使用时长增长的只有两处，都收口了；显存占用恒定。**

### 审计方法

不猜，直接看运行时现场：`Storage/ContextManager/` 下两份日志的实际大小与行数、
各定时器的创建/释放路径、出站队列的上界、每个集合的清理时机，以及界面里有没有 GPU 资源。

实测：`trace.log` 437 KB、`renderer-trace.log` **561 KB / 8841 行**。

### 1. 【真实缺陷】`renderer-trace.log` 完全没有上限

后端 `ContextTrace.Write` 有 600 KB 上限（超了读回、只留后半段重写），
前端 `trace()` 却是裸的 `appendFileSync`。而这个文件是**跨会话追加**的：
窗口每开一次多一行 `boot`，同一个 Storage 目录下所有历史会话共用一个文件 ——
实测那份日志的开头还留着更早构建号（`cm-app-2026-09-29-b`）的 boot 行。
长时间使用会一直涨，且每条 IPC 都要走一次**渲染主线程同步 IO**（自动拉全文时一次几百条）。

修法与后端对齐：`TRACE_MAX_BYTES = 512 * 1024`，超了就只保留后半段重写。
起始大小只用 `statSync` 量**一次**，之后自己累加字节数 —— 否则每条日志都要多一次系统调用。

### 2. 【真实缺陷】`Release()` 不摘 `LanguageModel.ContextTransform`

`AttachTransform` 往模型的 `ContextTransform` 属性上挂一个委托，那个委托是**闭包**，
捕获了 `activity` 和运行时实例。角色停用时 `OnActivityDeactivated` 会按模型逐个摘，
但**插件卸载**（`OnDestroy` → `Release()`）没有摘 —— 而 `DetachTransforms()` 明明早就写好了，
全项目却没有任何调用点。

后果（插件重载但不重启 Alife 时）：旧委托留在模型上 →
① 新运行时的 `AttachTransform` 因为 `property.GetValue(model) != null` 直接返回，
**插件覆盖静默失效**（`trace.log` 里只会看到 `attached=false`，界面上毫无提示）；
② 旧运行时被闭包引用着回收不掉，每重载一次泄漏一份。

修法：`Release()` 里补上 `DetachTransforms()`。

### 3. 顺带删掉死字段 `int references;`

`ContextAssemblyRuntime.cs` 里声明了 `int references;`，全项目只声明、从未读写。

### 审计结论：其余部分**不会累积**

| 对象 | 上界 / 清理时机 |
| --- | --- |
| 出站队列 `queue` | `OutboxCapacity = 256`，满则**丢新**消息并计数（不是无限堆积） |
| 卡死自愈线程 | `RecoverStalled` 最多换 2 次线程，不会无限造线程 |
| `watchdog` 定时器 | `Dispose()` 里释放；`thread.Join(800)` 限时等待，绝不无限阻塞 |
| IPC 监听器 | `IpcMain.RemoveAllListeners(ChannelId)`，且每个窗口一个独立通道 |
| 窗口 / 桥 | `OnWindowClosed` → 退订 + `bridge.Dispose()`；窗口反复开关不累积 |
| `historyRefreshTimer` / `autoAttachTimer` | 都在 `Release()` 释放；补试定时器最多补 3 次后自行 `Dispose` |
| `historySubscriptions` | 按 `ChatBot` 键；停用与卸载时移除（上界 = 角色数） |
| `transforms` | 停用角色 / 卸载插件时移除（本轮补上后者） |
| `snapshot` / `planState` | 每次 `state` 都是**整份替换**，旧对象直接可回收 |
| 前端 `autoPlanQueue` | 换角色 / 关开关时 `resetAutoFetch()` 清空 |
| `localStorage` 计划缓存 | 每角色一条；写不进去会被 `try/catch` 吞掉并退回本地草稿，不会崩 |

### 显存 / GPU

**恒定，不随使用时长增长。** 整个界面是 DOM/CSS：没有 WebGL、没有 `<canvas>`、没有 `<video>`，
唯一的 GPU 相关对象是那个 BrowserWindow 自身的合成开销（1280×820）。
「角色附件」页里的图片是普通 `<img>`，由 Chromium 的有界图片缓存管理，
`render()` 每次整份替换 `innerHTML`，旧节点随之释放。
（`app.css` 里的 `--canvas` 是个颜色变量，不是画布元素。）

### 验证

- 单元测试 319 项（新增第 16 轮 21 项：日志封顶、`DetachTransforms` 调用点、
  队列上界、定时器释放、无 WebGL、构建号四处同步）。
- 前端冒烟测试 46 组（新增 28n 组：把「已写入字节数」推到上限之上，
  断言下一次 `trace` 必须先重写且回落到上限以内）。

---

## 第十六轮（界面收尾：最小化图标、弹窗可点性、保存方案的命名、清掉死配置）

用户报的两处界面问题 + 一次配置清理。

### 1. 标题栏「最小化」图标画不出来（功能一直正常）

`.ic-min:after { content: ""; width: 10px; height: 1px; ... }` —— `:after` 默认是**行内级**盒子，
`width/height` 对它不生效，那条 1px 横线根本没渲染出来。
另外两个图标为什么没事：`.ic-max` 的边框画在 `<i>` 元素本身上（它是 grid 子项，被块级化了），
`.ic-close` 的 before/after 是 `position: absolute`（绝对定位会把盒子块级化）。
修法：给 `:after` 加 `display: block`，并把 `<i>` 做成 10×10 的 flex 容器让横线垂直居中。

### 2. 弹窗层加 `-webkit-app-region: no-drag`

Electron 里 `-webkit-app-region: drag` 是**窗口级**命中测试：标题栏那条拖拽区会吃掉压在它上面的
控件的点击，**与 z-index 无关**。弹窗遮罩铺满整个窗口（含顶部 42px 标题栏），
所以必须显式声明 `no-drag`，否则弹窗里落在拖拽区内的控件点不动。
顺带给 `.modal input/textarea` 补回 `user-select: text` —— 免得哪天有人给祖先加了 `user-select: none`，
输入框里的文字就选不中了（这类继承问题在浏览器里复现不出来，只在实际窗口里发作）。

### 3. 保存方案：把「这个名字会存到哪里」说出来

后端规则（`CharacterPresetPath`）：名称为空、**或名称正好等于角色名** → 写入「自动快照」槽；
只有别的名字才新建命名快照。而弹窗里预填的正是角色名 —— 用户改都没改就点保存，
以为新建了一份命名快照，结果列表里什么都没多出来（自动槽被覆盖了，界面上看不出来）。

现在输入框下面有一行**实时提示**：名称等于角色名时明确写「将写入自动快照槽……**不会**新建命名快照」，
换个名字就变成「将新建命名快照「X」」。另外打开弹窗即聚焦 + 全选预填的名字，想改名直接打字即可。

顺带把「新建角色」弹窗的 HTML `autofocus` 换成显式 `focus()` —— 动态插入的节点在 Electron 窗口里
不一定拿到焦点，显式调用更可靠。

### 4. 清掉 `ContextManagerConfig` 里 6 个没人读的字段

`IncludeMemoryHistory` / `IncludeNotes` / `IncludeSkills` / `IncludeGroupChat` / `MaxHistoryItems` /
`MaxPreviewChars` —— 全项目（含前端）**没有任何一行代码读它们**，框架配置面板上却显示得像真开关。
其中 `MaxPreviewChars` 造成的误解最深：旧代码曾「临时把它改成 220 再取摘要」，
看起来像在做短预览，实际取内容的一直是 `Limit()`（无条件返回全文），state 报文一直在推全文 ——
这正是「激活第二个角色把 IPC 桥顶断」的成因之一。删掉之后面板上只剩 5 个真生效的：
`OverrideMode` / `ActivePreset` / `UseCharacterCard` / `UseWorldBook` / `ApplyMacros`。

### 5. README 拆成两份

`README.md` 重写成**使用者文档**（定位、安装、界面导览、三种覆盖方式、配置界面、数据目录、
酒馆对应关系、已知边界）；按轮次的开发记录移到 `CHANGELOG.md`。部署脚本两份都发。

**验证**：单元测试 298 项 PASS；冒烟测试 **45 组** PASS（新增第 28m 组：最小化图标必须有实际尺寸且
伪元素是块级、保存方案弹窗要聚焦 + 全选 + 实时提示会存到哪个槽、弹窗层 `no-drag` 与 `.ic-min:after`
的源码级不变量）。

---

## 第十五轮（自动后台拉全文；并澄清门槛与预算的分工）

### 1. 自动后台拉全文：不用再手点「读取全文」

后端为了不把 IPC 桥顶爆，模块正文可能只随报文下发预览（`truncated`）。以前要用户自己点
「读取全文」—— 一张 143 模块的卡就等于 143 次手动点击，观感上就是「内容不全」。

现在打开装配页或世界书面板后，**只有预览的模块/条目会在后台自动逐条读回全文**。
两个硬性约束：

* **必须串行**：一次只发一个请求，等回包再发下一个。143 个请求一起发的话，它们的回包会一起
  挤进出站队列（限速 600 ms / 512 KB），既慢又容易顶到 1 MB 断连线。
  （`autoPlanFetching` 闸门 + `pumpAutoPlanFetch` 单条推进。）
* **必须有超时保护**：某一条没回包也要继续下一条（15 秒），不能因为一次丢包卡死整条队列。

世界书同理：首次下发里只要有「只有预览」的条目、或还有没读回来的条目，就自动开始分页读全文；
**没有截断也没有剩余时什么都不做** —— 否则每次保存世界书都要白跑一轮。

控制栏新增「自动读取全文」开关（`localStorage` 持久化，默认开）。关掉即回到「点了才读」；
换角色 / 关开关时会 `resetAutoFetch()` 清空队列（否则会拿旧 owner 的 id 去问新 owner 要正文）。

这只是把同一批正文**分多次**拿回来，完全在原有分页/限速机制内，**不碰任何体积红线**。

### 2. 澄清：门槛 ≠ 预算（门槛保持 2000 不动）

用户问「为什么要设门槛，把门槛提到 1 万字行不行」。答案是：

| | 作用 |
| --- | --- |
| **总字节数** | 决定「**能不能全显示**」。448 KB ÷ 6 字节 ≈ **7.6 万字**，全部正文在此以内一条都不截 |
| **门槛（2000 字）** | 只在装不下时才有意义，决定「**牺牲谁**」 |

而且**提高门槛会让更短的正文被截**。实测（100 条 × 8000 字 + 1 条 1500 字）：

| 门槛 | 那条 1500 字 | 100 条长条目 |
| --- | --- | --- |
| 2000（现在） | **完整保留** | 截到 749 字 |
| 10000 | 被截到 756 字 | 截到 756 字 |

原因：门槛是「必须完整保留」这条线的下界。抬高它会把 2000～10000 字这一大批正文也划进
「必须全留」的集合，这个集合自己就超预算 → 保护档整个失效 → 掉进「全体水填平」→ 所有正文一起被砍。

这套换算已固化成 **16 条断言**（第 15 轮），包括「门槛 0/2000/10000/20000/100000 在总量够时都不截」
和「门槛 10000 反而让 1500 字被截」。以后谁再想拍脑袋改门槛，测试会直接拦住。

## 第十四轮（「正文太大」的门槛统一到 2000 字 + 预算水填平）

用户的原话（逐字）：

> 这条正文太大，报文里只带了预览（全文 244 字）。这里先设为只读：编辑预览没有意义，保存时后端会按 Id 用磁盘上的原文回填。  什么叫两百字正文太大，统一改成两千字

### 1. 244 字被叫「正文太大」—— 根因不是门槛低，是分配方式错了

旧逻辑按模块在列表里的**先后顺序**逐条喂预算：排在前面的长模块把 448 KB 吃光，
排在后面的短模块只剩几十字节，于是出现「正文太大（全文 244 字）」这种荒谬提示。

截一条 244 字的正文只省下 144 字节，却让用户以为自己的设定丢了。**修法：水填平。**

### 2. 门槛统一到 2000 字（`ContextStateBudget.MinKeepChars`）

不超过 **2000 字**的正文一律原样下发，绝不被截成预览。前端文案也把这个门槛说出来
（`TRUNC_KEEP_CHARS`），免得用户再看到「244 字太大」这种提示。

### 3. `FairCap` 水填平：预算在长正文之间平均分，短正文受保护

`FairCap(texts, budgetBytes, floorChars)` 分两档：

1. **够用就保护短正文**：≤ 2000 字的全留，只在长正文之间水填平；
2. **实在不够就全体水填平**：`BinaryCap` 二分出最大的**统一**上限，所有超限正文截到同一个长度。

计划路径 `ShrinkPlanForWire` 用 `ref cap` 跨 pass 传递，**统一上限只降不升** ——
否则会出现「缩水 → 实测又超 → 再缩」的震荡。世界书路径同样两阶段：
先用元数据（`WorldBookMeta`）定下「这一页装得下哪些条目」，再按剩余预算统一截正文。

`FitText` 也不再往 `{4000, 2000, 800, 400, 220, 80, 0}` 档位上凑，改为取「能放下的最长前缀」
（244 字在 1000 字节下现在留下 166 字，而不是旧档位的 80 字）。

### 4. 一个自己踩出来的坑：世界书分页会漏条

`PackWorldBookRows` 重写后一度返回**元数据游标** `index`，但正文循环会因放不下提前 `break`，
于是这一页没发出去的条目被整段跳过（静默漏条）。现在返回 `start + list.Count`
（按**实际下发了多少条**推进），并写了注释说明为什么不能用元数据游标。

## 第十三轮（右栏模块搜索、多条开场白、自动宏应用、内容放大）

用户提的四点（逐字）：

> 在没有选择具体模块内容的时候，右边栏显示的是模块编辑单击模块在这里修改。这些内容，那可以加一个搜索框可以搜索中间的模块信息然后快速定位；有些卡的角色开场白不止一条吧，虽然默认都选择的第一条，但其他如果也可以分开显示就好了；然后再宏应用的界面里加一个自动宏应用，char默认等于角色名，然后像是其他比如lastPrompt就等于lastPrompt，毕竟本身没有酒馆那么丰富的宏能力，所以填本身就可以了；在编辑大窗口里内容区块旁边加一个放大按钮，可以放大到整个编辑窗口，可以顶掉前面比如模块名称XmlFunctionCaller #1输出身份system区域功能模块区域内顺序什么的；

### 1. 右栏「模块搜索」：143 个模块不用再肉眼滚

第十二轮修好「导入后装配页一片空白」之后，`【Sgw】又看一集.png` 那种卡导入进来就是 **143 个模块**。
中间列一屏放不下几个，右栏没选中模块时又只有一句「单击模块在这里修改」——
想找某一条世界书条目只能靠肉眼滚，等于没有导航。

现在右栏**任何状态下**顶部都有一个搜索框（`moduleSearchHtml()`），可搜：

| 搜索范围 | 说明 |
| --- | --- |
| 模块名称 | 命中排最前（完全相等 > 前缀包含） |
| 正文 | 命中处高亮，前后各留一段上下文 |
| 触发关键词 / 次要关键词 | 世界书条目按关键词找 |
| 来源 / 区域 / 身份 | 例如搜「世界书」「功能模块」「assistant」 |

点搜索结果 → 选中该模块（右栏切成编辑表单）+ 中间列那张卡片滚进视野并高亮 1.5 秒
（`.jump-flash`），搜索词自动清空。回车 = 跳到第一条命中。

两个实现细节值得记下来：

* **输入时只重绘结果容器**（`#moduleSearchResults`），不重绘整个 `#inspector` ——
  否则每敲一个字都会重建输入框、焦点丢掉，一个字都打不下去。
* 搜索框是**包在 `renderPlanInspector` 外面**插进去的，不是在四个分支里各写一遍。
  那个函数有 4 个出口（运行时消息 / 空态 / 官方角色设定 / 普通模块），逐个插入迟早漏一个。

### 2. 多条开场白：不再只取第一条

酒馆角色卡的 `first_mes` 只是**第一条**开场白，作者另写的备选开场在 `alternate_greetings` 里
（v2/v3 规范是字符串数组，实测还有写成对象 `{"0":"…"}` 或裸字符串的）。以前只读 `first_mes`，
其余开场在导入时被静默丢掉 —— 用户看得见卡里有好几条，导入后只剩一条，且没有任何提示。

新增 `TavernImport.Greetings(cardData)`：把 `first_mes` + `alternate_greetings` 全读出来，
统一编号成「开场白 / 开场白 2 / 开场白 3 …」，过滤空串、去掉与第一条重复的内容。
`AlternateGreetings()` 是它去掉首条的结果。

两条导入路径都接上了：

* **装配页 →「角色卡」**：每条额外开场白单独成一个模块（`角色卡 · 开场白 2`、`开场白 3`…），
  想用哪条勾哪条，其余取消勾选即可。导入完成的提示会写明「这张卡有 N 条开场白，已分别导入」。
* **「导入酒馆资源」**（角色库那条路）：全部写成世界书条目，`InsertionOrder` 依次 +1。

同时把额外开场白暴露成宏，预设里可以直接引用：`{{greeting}}`（第一条）、
`{{greeting2}}`、`{{greeting3}}`…，另有 `{{alternateGreetings}}`（额外开场用空行拼接）与
`{{greetingCount}}`。

### 3. 宏设置加「自动宏应用」

本插件没有酒馆那么丰富的宏能力：`{{lastPrompt}}`、`{{time}}`、`{{random}}` 这些需要运行时状态的宏
根本没有实现。以前它们会**原样保留**在提示词里，并在预览/应用后弹一屏「未识别的模板宏」，
用户只能一条条手工填进「自定义宏」。

现在 `ContextPlan.AutoMacros`（**默认开**）控制一条规则：

| 宏 | 取值 |
| --- | --- |
| `{{char}}` | 角色名（`index["Name"]`） |
| `{{user}}` | 宏设置里填的名字（默认 `User`） |
| `{{description}}` / `{{personality}}` / `{{scenario}}` / `{{system}}` / `{{mesExamples}}` / `{{postHistoryInstructions}}` / `{{greeting}}` / `{{greeting2}}`… / `{{charCreatorNotes}}` | 角色卡里对应的字段 |
| `{{lastPrompt}}`、`{{time}}` 等**本插件没实现、用户也没定义**的 | 直接填成宏名本身（`lastPrompt`、`time`） |

带参数的宏只取参数前的名字：`{{random::a::b}}` → `random`，免得把一串参数当正文写进提示词。
注释宏（`{{!…}}`、`{{//…}}`）仍然整个删掉，不会被「填成自己」复活。
关掉这个开关就恢复旧行为：原样保留 `{{lastPrompt}}` 并在预览里给出清单。

界面上有两处入口：装配页控制栏的「自动宏应用」勾选框（改完立即保存），
以及「宏设置」弹窗里的同一项（带规则说明）。可用内置宏清单也补上了 `greeting2` / `greeting3` /
`alternateGreetings` / `greetingCount`。

### 4. 编辑大窗口的「⤢ 放大」

长正文（`Sgw_2.png` 那种单条 3 万字的开场白）在大窗口里编辑时，
「模块名称 / 输出身份 / 区域 / 区域内顺序」四个字段只是占地方。
现在内容区块旁多了一个「⤢ 放大」按钮：点一下把内容编辑器顶到整个弹窗、
隐藏前面所有字段（`.modal-body.editor-expanded`），按钮变成「⤡ 还原」，再点一次回去。

纯视图切换，不动任何数据；关掉窗口重开会回到默认布局。运行时消息的编辑大窗口用的是同一套。

### 验证

* 单元测试 **298 项**（新增 13.1–13.6：开场白全集读取与编号、`alternate_greetings` 的三种形状
  （数组 / 对象 / 裸字符串）、空 `first_mes` 时额外开场顶上、无开场白时返回空集、
  `{{greeting2}}`/`{{greeting3}}`/`{{alternateGreetings}}`/`{{greetingCount}}` 取值、
  自动宏填宏名本身（`{{lastPrompt}}`→`lastPrompt`、`{{random::x::y}}`→`random`）、
  自动宏不会盖掉真有取值的宏、注释宏不会被复活、关掉后恢复「原样保留 + 回报」、
  `autoMacros` 默认开、过 IPC 是 camelCase、落盘往返、老计划仍默认开、
  端到端编译出「原样保留」与「填成宏名」两种请求）；
* 冒烟测试 **44 组**（新增第 28j 组：右栏搜索框在空态也常驻、搜模块名/正文/未参与装配的标记、
  命中高亮、点结果选中模块并给卡片高亮、跳转后清空搜索词、
  放大按钮隐藏「模块名称/输出身份/区域/区域内顺序」且内容编辑器真的变高、
  再点还原、宏设置里「自动宏应用」默认勾选且取消后随 `plan:save` 发回后端、
  以及 5 条源码级不变量）。

## 第十二轮（装配计划与世界书也走预算装填；「卡内世界书」确认弹窗改成插件自己的样式）

用户报的两个问题（逐字）：

> E:\Download\Chat-WenDa\AA酒馆AA\【Sgw】又看一集.png导入后显示导入完成，但有字段被跳过
> ×
> 卡内世界书已一并导入 142 条。 这张卡没有填写标准的提示词字段（描述 / 性格 / 场景 / 系统指令 / 对话示例 / 历史后指令），已按「基本信息」导入：只用上了开场白 / 作者注释等能读到的内容，之后可再补设定或导入世界书。；但结果什么也没有；；还有一个导入后显示：一条「worldbook-state」数据有 864 KB（转义后），超过安全上限 576 KB，已阻止发送以免把 IPC 桥卡死。请减少该角色参与装配的上下文条目后重试；E:\Download\Chat-WenDa\AA酒馆AA\Sgw_2.png

两件事的根因是同一个：**`plan-state` 和 `worldbook-state` 没有走预算装填。**

`state` / `character-bundle` / `items:page` 早就有「按预算装填 + 分页」的机制（`ContextStateBudget.FillToBudget`），
但计划报文和世界书面板报文是整份发出去的。实测：

| 角色卡 | 世界书条数 | 正文 | 卡 JSON |
| --- | --- | --- | --- |
| `【Sgw】又看一集.png` | 142 | 98,837 字（计划 143 个模块） | 566,977 字符 |
| `Sgw_2.png` | 40 | 77,648 字 | 925,063 字符 |

汉字经 `System.Text.Json` 序列化后是 6 字节（`\uXXXX`），所以 9.9 万字 ≈ 594 KB、7.8 万字 ≈ 466 KB。
再加上模块元数据，`plan-state` 会到 610 KB、`worldbook-state` 会到 864 KB —— 全部超过
`MaxWireBytes`（576 KB），被 `ContextIpcBridge` 直接拦掉。

### 1. 装配页空白：计划被拒发，错误弹窗又被「导入完成」顶掉

旧逻辑里 `SendPlan` 一旦发现超预算就 `SendWindow("plan-error")` 拒发；
而 `ImportPlanModules` 紧接着又发 `plan-imported`（「导入完成」）。
两个都是模态弹窗，**后到的把先到的顶掉** —— 用户只看到「导入完成，但有字段被跳过」，
装配页却一片空白。

修法三条，缺一不可：

1. **计划不再拒发，改为瘦身下发**（`ContextStateBudget.ShrinkPlanForWire`）。
   模块的 `id` / `name` / `role` / `group` / `source` / 关键词**一个都不能少**（装配页靠它们渲染结构），
   正文放得下给全文、放不下沿 `PreviewLadder` 缩水并打 `truncated` + `contentLength`。
2. **回包顺序固定为「先 plan-state，再 plan-imported」**。任何失败提示都必须晚于成功提示，
   否则照样会被顶掉。
3. **写回路径按 Id 从磁盘回填正文**（`ContextAssemblyRuntime.RestoreTruncatedModules`）。
   前端只持有预览也不会丢数据：`plan:save` / `plan:preview` / `plan:apply` 都会先
   按 `Id` 从 `Plans/*.json` 把全文取回来，取不回来就抛错中止，绝不把预览写进磁盘。

### 2. 世界书面板：864 KB 被拦 → 结构全给、正文按需读

`worldbook-state` 同样改成预算打包（`ContextStateBudget.PackWorldBookRows`）：

* `fullText=false`（打开面板）：**40 条一条都不能少**，正文放不下给预览并打 `truncated`。
  用户至少能看到「有哪些条目、关键词是什么、开没开」，而不是一片空白。
* `fullText=true`（`worldbook:page`，点「读取全部正文」）：装到预算满就停，
  回传 `nextOffset` 让前端翻页，**每页都是完整正文**；第一页至少出一条，
  否则 `nextOffset` 不前进、前端死循环。

### 3. 前端：预览必须一眼看得出来，而且不能改

这是最容易埋雷的地方 —— 如果用户把预览当正文改，保存时后端按 Id 回填原文，
**编辑就白做了**。所以：

* 卡片/条目上打「预览 · 全文 N 字」标记；
* 编辑框设 `readonly`，并说明「只读 · 正文未随报文下发」；
* 提供「读取全文」（单条，走 `plan:module` / `worldbook:entry`）与
  「读取全部正文」（分页，走 `worldbook:page`）；
* `readPlanFields` / `bindWorldEditors` 在截断时**跳过**正文写入；
* `keepLoadedModuleContent`：报文瘦身是每次下发都做的，所以用户读回全文后
  下一次 `plan-state` 里那条又会是预览 —— 把本地那份贴回去，别让界面退回去；
* 世界书保存照常提交（后端按 Id 保留磁盘原文），只是状态栏说明「其中 N 条正文是预览」。

### 4. 顺带修掉一个自己写出来的坑

`ShrinkPlanForWire` 第一版是**一次算完**的，结果缩完还超预算 1,177 字节 ——
因为「缩水」本身给每条被截断的模块**新增**了 `contentLength` / `truncated` 两个字段。
现在改成按实测值迭代收敛（最多两三轮）。另外重构时漏掉了「装得下也要扣减 `available`」
一行，导致预算永不减少、全都不截断 —— 单元测试里那条 143 模块的用例就是专门盯这个的。

### 5. 「卡内世界书」确认弹窗：不再用 Windows 原生对话框

原来的实现是 `Electron.Dialog.ShowMessageBoxAsync` —— 那是 **Windows 系统对话框**：
灰底、系统字体、系统按钮布局，标题栏还写着应用名，和插件界面完全不是一套，
用户一眼就看得出「这是外来的东西」。

改成「**后端问、前端答**」：

```
后端  SendWindow("card-worldbook-ask", { requestId, displayName, bookCount, preview })
前端  showCardWorldbookAsk() → 插件自己的 openModal（窄弹窗 + 条目预览 + 记住我的选择）
前端  send("card-worldbook-answer", { requestId, accepted, remember })
后端  AnswerUiAsk() → TaskCompletionSource 放行 await
```

两条时序约束：

* **await 绝不能持 `stateLock`**（和文件对话框同理）——弹窗仍在锁外等；
* **必须能收场**：用户把插件窗口关掉时永远不会有人回答，所以 `AskUiAsync` 每 5 秒检查一次
  窗口是否还活着，关了就当「没选」放行，绝不让导入流程永久挂住。

顺带修掉一个会误导用户的超时：弹窗打开期间，前端把待回请求的看门狗统一推后
（`postponePendingRequests`）——用户在思考时，后端没丢包也没卡，用默认的 10 分钟去判
「后端无响应」纯属误报。

关掉弹窗（点 × / 点遮罩）等价于「只要角色卡」，而且**也会回答后端** ——
否则后端的 `await` 会一直挂着。

### 验证

* 单元测试 **237 项**（第十二轮新增 12.1–12.8：转义字节口径、小计划不截、143 模块真实计划缩进预算
  且结构完整、1 KB 极端预算只缩正文不删模块、40 条世界书首屏全在、分页每页都是完整正文
  且不重不漏、单条超整页也不会死循环、offset 语义）；
* 冒烟测试 **28 组**（新增第 28h 组：预览标记与全文长度、点「读取全文」只发一个请求、
  预览编辑框只读、读回全文后可写、读回后不被下一轮 `plan-state` 退回预览、
  世界书条目同样三件事、「读取全部正文」从 offset 0 分页、分页状态复位，
  以及 8 条源码级不变量）。

## 第十一轮（修掉导入角色卡必崩的双重规范化 + 卡内世界书改为弹窗确认）

用户报的崩溃（逐字）与两条新需求：

> 导入装配资源失败 momo
> System.ArgumentException: Accessed JArray values with invalid key value: "entries". Int32 array index expected.
> at Newtonsoft.Json.Linq.JArray.get_Item(Object key)
> at Marisa.ContextManager.TavernImport.EntryRows(JToken book) in ...\TavernImport.cs:line 101
> at Marisa.ContextManager.TavernImport.NormalizeEntries(JToken book) in ...\TavernImport.cs:line 131
> at Marisa.ContextManager.TavernImport.MergeWorldBook(JObject existing, JToken incoming) in ...\TavernImport.cs:line 168
> at Marisa.ContextManager.ContextManagerRuntime.MergeCardWorldBook(String characterDirectory, JToken cardBook) in ...\ContextAssemblyRuntime.cs:line 636
> at Marisa.ContextManager.ContextManagerRuntime.ImportPlanFile(String owner, JsonElement payload) in ...\ContextAssemblyRuntime.cs:line 600

| 问题 | 现在 |
| --- | --- |
| **导入任何带 `character_book` 的角色卡都必崩**（`Accessed JArray values with invalid key value: "entries"`） | 根因是**双重规范化**：`MergeCardWorldBook` 先把卡内世界书 `TavernImport.NormalizeEntries(cardBook)` 变成 **JArray**，再把这个 JArray 交给 `MergeWorldBook(existing, incoming)`；而 `MergeWorldBook` 内部**又调了一次** `NormalizeEntries` → `EntryRows` 收到的是一整个 JArray，于是它按字符串键去取 `book["entries"]`，在 JArray 上抛 `System.ArgumentException`。**不是某张卡畸形，是每条路径都会炸**（全盘扫过 14,860 个文件，`character_book` 为数组的文件数为 0）。修法三层：① `MergeCardWorldBook` 改为直接把**原始** `cardBook` 传给 `MergeWorldBook`；② `MergeWorldBook` 的形参改名 `incomingEntries`，注释写明「规范化是这一层的事，调用方不要预先规范化」；③ `EntryRows` 加**形状守卫** —— 认不出形状就返回空集，绝不抛异常（导入流程不该因为一次误用或一张畸形卡炸掉整个操作）。单元测试把「规范化后的 JArray 直接喂给 `MergeWorldBook`」这个崩溃现场原样复现并锁住。 |
| **卡内世界书用工具栏勾选框，用户没法预判**（用户：「不应该是在导入的角色卡的时候，当检测到有世界书那就再一个弹窗确认是否需要导入该卡世界书？来让用户选择吗」） | 勾选框删掉了。现在**选完文件、后端把卡读进来之后**才判断：`EntryRows(card.character_book)` 有条目就弹一个原生确认框（Electron `MessageBoxOptions`），标题写「『灰风』内嵌了 6 条世界书条目」，正文列出前 6 条**条目标题**做预览，按钮是「一起导入 / 只要角色卡」，下面带复选框「**记住我的选择（以后不再询问）**」。装配页的「角色卡」和酒馆兼容页的「导入角色卡」**共用同一套策略与记忆**（以前后者是无条件并入，用户没有拒绝的机会）。弹窗关掉（Esc / 点 ×）按「只要角色卡」处理，而不是取消整个导入 —— 用户已经选好文件了，把角色卡导进来才是本意。 |
| **策略没有落脚点，每次都要重新做决定** | 新增 `Storage/ContextManager/ImportOptions.json`，只存 `cardWorldbook: ask / always / never`（默认 `ask`）。没塞进 `ContextManagerConfig`：那是模块级配置（覆盖模式 / 预设 / 宏开关），改它会牵动配置面板的读写与迁移；这里只是一个三行 JSON 的小文件，读失败就退回默认值，不影响导入本身。策略的**可视入口放在酒馆兼容页**（「导入选项 → 卡内世界书」下拉框），选完立刻写回并刷新说明文字，随时能改回「每次询问」。 |
| **世界书面板保存会丢掉酒馆专属字段**（顺带修） | 面板只编辑 7 个字段（标题 / 关键词 / 正文 / 启用 / 常驻 / 顺序 / ID），但 `SaveWorldBook` 是**按面板字段把整份文件重建**的 —— 从酒馆导入的世界书，第一次点保存就会丢掉 `position` / `depth` / `probability` / `secondary_keys` / `extensions` 等所有面板管不到的字段。现在改成**原地修改原始条目**（先按 ID 找，找不到再按「标题 + 正文」找；老文件没有 `id` 时面板会现生成一个 Guid，那种 id 是找不到原始行的），面板字段照常覆盖，其余字段原样保留。另有一个隐蔽的坑：`TavernImport` 读取时**酒馆键优先**（`Title` 取 `comment ?? name ?? Title`，`Enabled` 还要再 `&& !disable`），所以原地改之前必须先删掉这些「优先级更高的别名」（`StripShadowKeys`），否则用户在面板里改的标题 / 启用状态会被残留的 `comment` / `disable` 覆盖回去 —— 表现就是「改了但没生效」。保存前也会 `backup-时间戳` 了。 |

## 第十轮（角色快照带上设定来源 + 酒馆卡导入的「特殊卡」与卡内世界书）

用户的五条原话（逐字）：

> 既然是角色快照，所以我希望把角色设定里的内容也包括酒馆的预设世界书角色卡的设定内容也存进去；；还有就是导入角色卡的时候，角色设定这一层级的显示就消失了，我点击角色卡不参与装配，然后它才会出现，后面它就能一直出现了，这显示问题；还有界面的预览保存草稿应用到当前角色这些按钮的左边的文字CONTEXT RECIPE上下文装配什么的可以换成讲解插件覆盖和本地覆盖的区别；还有有些角色卡是有世界书的，但是没有选项让导入的时候是否也导入卡里的世界书，比如:E:\Download\Chat-WenDa\AA酒馆AA\灰风.png，还有一些角色卡无法导（当前角色的酒馆卡里没有可导入的提示词字段（描述 / 性格 / 场景 / 系统指令 / 对话示例 / 历史后指令 全为空）。）比如：E:\Download\Chat-WenDa\AA酒馆AA\Sgw_2-gemini.png，因为一些卡比较特殊，但可以适配基本信息就可以了

| 问题 | 现在 |
| --- | --- |
| **角色快照只存「配方」不存「原料」** | 快照（`CharacterPresets/*.json`、导出的 `.contextpreset.json`）以前只有装配计划。可模块正文里大多是 `{{description}}` / `{{worldbook}}` 这类**宏**，取值来自角色目录下的 `TavernCard.json` / `WorldBook.json` 和 `ContextManager/Presets/<名>.json` —— 只带走计划，换角色/换机器后宏全部展开成空。现在新增 `sources` 字段，把**酒馆卡原文 + 世界书原文 + 酒馆预设原文及其名称**一起打包；读取/导入/计划丢失自动恢复时都会回填（**只补缺失的文件，绝不覆盖本机已有的**，覆盖前先 `backup-时间戳`）。快照卡片会显示「含设定来源：世界书 N 条 / 酒馆卡 / 预设 X」，没抓到来源的会明确写「只含装配配方」，不让用户误以为它是完整的。 |
| **导入角色卡后「角色设定」层级忽隐忽现** | 根因是**两处逻辑互相打架**：`ImportPlanModules(kind=card)` 里有一句 `plan.Modules.RemoveAll(Source=="native")`（注释写着「Tavern mode replaces the native character block」），而 `PreparePlan` 又有一条「计划里一个 native 模块都没有就补一个官方系统消息模块」的兜底。于是导入后「角色设定」分区立刻消失（空分区不渲染），**下一次保存/应用**（比如用户随手取消一个模块的「参与装配」勾选）后端又把 native 补回来 → 用户看到的就是「点一下才出现，之后一直在」。现在**不再删除** native 模块：重复内容改由 `skippedByNative` 判重跳过（正文与「角色设定 #0」逐字相同时不重复导入），并在提示里说明。冒烟测试直接读源码断言这句 `RemoveAll` 不许再出现。 |
| **预览/保存草稿/应用到当前角色左边的文字没讲清两种覆盖方式的区别** | 装配页头部原来只写「模块从上至下依次写入请求」，用户看不出「插件覆盖」和「本地覆盖」差在哪 —— 一个只改请求、一个改角色文件，选错的代价完全不同。现在直接在按钮左边讲清楚：**插件覆盖**不动角色文件、只在请求前重排、随时关掉就恢复；**本地覆盖**写进 `index.json` 的 `Prompt`、插件关掉也生效、但每改一次都要重新「应用到当前角色」。 |
| **卡内世界书没有导入选项**（例：`灰风.png` 带 6 条 `character_book.entries`） | 导入栏新增「**带卡内世界书**」勾选项（默认开，选择记住）。勾选时一次操作做完两件事：① 把 `data.character_book` **合并**进角色目录的 `WorldBook.json`（判重按「标题 + 正文」，用户已有的条目一律保留，导入是「加上去」不是「换掉」）；② 把条目作为 `source=worldbook` 的模块加进装配。顺带修掉一个老 bug：以前把酒馆世界书**原文**直接写进 `WorldBook.json`，而世界书面板读的是大写 `Entries`，酒馆用小写 `entries` → 「导入成功但面板里一条都没有」。现在统一走 `TavernImport.NormalizeEntries` 规范化后再落盘，读盘也走同一条路径。 |
| **「特殊卡」提示词字段全空就直接拒绝导入**（例：`Sgw_2-gemini.png`） | 实测两张被抱怨的卡**并不是真的什么都没有**：`灰风.png` 的 `description` 有 252 字；`Sgw_2-gemini.png` 的 6 个标准字段全空，但 `first_mes` 有 **32,051 字**、还带 40 条卡内世界书。旧逻辑只认那 6 个字段，全空就抛「没有可导入的提示词字段」。现在可用字段扩到 8 个（6 个标准字段 + **开场白** + **作者注释**），全空时才报「没有任何可用文本」，并且会在提示里说明「已按基本信息导入」。创建角色入口（酒馆兼容页）也同步：6 个标准字段全空时退回用开场白/作者注释拼 `Prompt`，世界书改为「合并卡内世界书 + 补一条开场白」，不再覆盖写。 |

## 第八轮（按预算装填 + 分页加载；并修掉出站合并吃掉回包导致的 15 秒超时）

上一轮把预算从 224 KB 调到 384 KB，方向对了一半：**"调大预算"只是把爆点往后推，不是解药**。
用户的原话是：

> 虽然是我最大的角色，但那也只是我使用的一天的量，如果实在太多，可以不一次性完全加载，按照分页加载不行吗？

于是这一轮把策略换成 **按预算装填 + 其余走分页**。

| 问题 | 现在 |
| --- | --- |
| **`worldbook:get` 等小请求「在 15 秒内没有收到后端响应」**（插件跑一段时间后随机出现） | 根因是**出站合并（coalesce）把回包吃掉了**。`ContextIpcBridge.Coalescible` 原来列了 6 个类型，但其中 `worldbook-state` / `presets-list` / `charpreset-list` / `plan-state` / `native-prompt` **全都是某个请求的回复**，而前端 `noteReply` 的语义是「一条回复核销一个待回请求」。出站队列被 354 KB 的 `state` 和 329 KB 的 `character-bundle` 顶住时（限速 512 KB/600ms，一个周期就要 1.4s），两次 `worldbook:get` 的回包在队列里撞在一起被合并成一条 → 前端 15 秒后误报超时。现场日志完全对得上：`<- worldbook:get` 两次、`-> worldbook-state` 只有一次。现在 `Coalescible` **只留 `state`**（唯一「最新一份即完整状态、丢旧的无损」的类型），并且前端把 `state` 声明为**广播回复**（一份 state 核销所有在等 state 的请求），两侧都有断言锁住。 |
| **每次 `state` 推送都重拉一遍 300+ KB 的大回包**（把出站队列顶住，是小回包被推迟/合并的诱因） | `state` 已经带上「当前查看角色」的完整条目（后端 `BuildOwnerItems` + `fullContent:true`），但前端在 state 处理器里无条件清空 `loadedOfflineOwner`，导致每次推送都重发 `character:load`（329 KB）+ `worldbook:get` + `native-prompt:get`。现在只有 state 里**确实没有**该角色的条目时才回退到按需加载。冒烟测试直接断言「state 推送后不再出现 `character:load` / `worldbook:get`」。 |
| 出站队列堆积在日志里完全看不见 | 新增两条日志：`outbox backlog queue=N`（队列超过 24 条时记一次，排空后重新武装）和 `outbox coalesced type=... total=N`（真的发生合并时记一次）。以前这两种情况都是**静默**的，只能靠翻报文数反推。 |
| **预算调大之后仍然会撞上限**（一个角色一天 114 条实时消息 + 83 个归档，用一个月就是几千条） | 不再假设"所有条目都塞得进一条报文"。新增 `ContextStateBudget.FillToBudget`：按优先级逐条装填，装得下就给**全文**，装不下就只带元数据并打 `Truncated`（界面显示「正文未随状态推送加载 · 读取全文」），连元数据都装不下时计入 `omitted` 并提示「还有 N 条更早的条目未加载」，点一下走 `items:page` 按需取。**"一条报文最多多大"由预算硬保证，与用户用了多久、攒了多少条无关。** |
| **上一版把正文截成 400 字，比不修还糟**（用户："你现在搞截断导致消息都显示不完整，还不如上一版"） | ① 预算 384 KB → **448 KB**；② 降级阶梯起点 400 字 → **4000 字**（绝大多数聊天消息都在这个长度以内，不会"刚超一点就掉成 400 字预览"）；③ `state` / `character-bundle` 都改成 **`fullContent: true`**，给全文，由 `FillToBudget` 决定谁进得来 —— 给全文不会让报文变大，只会让**装得下的那些显示完整**。 |
| **`character-bundle` 涨到 635 KB 被硬上限拦掉，界面报"体积过大"**（用户："各种报错 ipc 什么体积过大什么的"） | 找到真正的肥元凶：`ExtractAttachments` 把**整条消息正文**又复制一份塞进 `Attachments[].Detail`。后果有两层：正文在报文里出现两次（bundle 涨到 635 KB），而且降级阶梯只清 `Content`、不动 `Attachments` —— **降级是假的**（声称"正文全部改为按需读取"之后报文仍有 317 KB）。现在 `TextContent` 只放短预览，且 `TruncateItems` / `CloneWithoutContent` 一并清附件 `Detail`。bundle 本身也走 `FillToBudget`。 |
| **只按时间排序会把"小但关键"的条目挤出去** | 新增装填分档 `ContextStateBudget.FillTier`：`character-prompt` / `offline-system` / `live-system` / `global-config` 为第 0 档，永远先进报文；其余第 1 档。否则"三个月没改角色设定"就会让角色 Prompt 被最新几条聊天挤出报文，**提示词工作室和角色管理页直接空白** —— 这比消息分页严重得多。 |
| **分页 offset 与后端切页口径不一致会漏条** | `state` 装填、`character-bundle`、`items:page` 三处**共用同一个** `ContextStateBudget.OrderForFill`（当前查看角色优先 → 配置类优先 → 更新时间倒序）。后端回传 `nextOffset`，前端原样传回；剩余条数 `remaining` 也由后端算（前端自己数会被去重影响而算错）。单元测试锁住"两页拼起来 == 全集且不重不漏"。 |
| **分页条显示的欠账是全局合计，不是当前角色的** | `state` 新增 `omittedByOwner`（按角色拆开），分页条说的是「"星野雨"共 250 条，还有 155 条更早的条目未加载」，不会把别的角色的欠账算到当前角色头上。 |
| **`character-prompt` 条目其实从来没进过 `state`** | 上一轮把 `BuildOwnerItems` 抽出来时漏掉了 `AddEditableCharacterPrompt`，于是 `sourceKey === "character-prompt"` 的条目根本不存在 —— 提示词工作室的文本框、角色管理页的角色摘要、装配页的「角色卡独立系统提示词」分组全都读不到数据。现在补回，并且它是第 0 档、体积小，不会被分页挤出去。 |
| **降级/分页必须"看得见"** | `state.degradeSteps` + `hiddenItems` 走页面头的降级提示条；`omittedByOwner` 走对话页 / 记忆页 / 附件页底部的分页条；总览页列出每个角色还有多少条没加载，点角色名直接跳到该角色的对话页继续读。**所有会渲染条目的视图都有分页入口，不会出现"点了没反应"的死按钮。** |

## 第七轮（报文体积预算 + 出站限速与合并 + 截断内容保护）

| 问题 | 现在 |
| --- | --- |
| **在插件窗口里激活第二个角色 → 整个 Alife 卡死，主窗口弹出 `Rejoin failed... trying again in 1 second`** | 找到了**确定的根因**：不是角色激活失败，而是 `state` 报文太大把 Electron.NET 的 IPC 桥**断开**了。激活第二个角色时后端给**每个已激活角色**都内联一份装配计划 + 提示词，`state` 从 13,235 字符涨到 216,535 字符（≈850 KB，是 Socket.IO 1 MB 上限的 85%），而 Engine.IO 的 polling 传输会把排队的数据包**合并成一个 HTTP 请求体**，两条 850 KB 拼成 1.7 MB → `413` 断连 → **两端都没有重连逻辑** → 永久卡死。现在：`state` 只给**当前查看的那个角色**内联计划与提示词，超预算按阶梯降级，出站**限速 + 合并**，并有单条报文硬上限。详见「IPC 桥为什么会整程序卡死」。 |
| 报文体积一直没被发现，因为按**字符数**记的日志严重低估 | SocketIOClient 用 `System.Text.Json` 序列化，**所有非 ASCII 会被写成 `\uXXXX`（一个汉字 6 字节）**。所以日志改成记**真实转义后的字节数**：`-> state bytes=...`、`state push: ... bytes=N budget=M steps=[...]`。 |
| 预算和硬上限本身也低估了近一倍（**口径不一致**） | `Measure` / `MaxWireBytes` 一开始量的是 **UTF-8 字节数**，而真正写上线的是**转义后**的字节数。后果很严重：名义 512 KB 的硬上限，实际会放行约 920 KB —— 正好贴着 1 MB 的断连线，保护形同虚设。现在统一由 `ContextStateBudget.EscapedBytes` 逐字符精确计算（`"`/`\` 2 字节、控制字符与所有非 ASCII 6 字节），预算、硬上限、计划内联阈值、限速窗口全部走同一个口径。 |
| 条目**数量**本身就能把报文顶爆 | 每条条目光 JSON 字段名就要约 450 字节：正文全部清空后，400 条仍有 180 KB。原来的降级阶梯最后一档是"正文清零"，遇到大量条目就走到了尽头。现在加最后一档**按数量裁剪**（保留最近更新的 N 条），并把 `hiddenItems` 告诉界面，由界面说明"另有 N 条因报文体积限制没有列出"。有了这一档，「任何快照都能落进预算」这个不变量才对**任意条目数**都成立 —— 这是"插件永远不会顶断 IPC 桥"的最后一道保证（测试覆盖到 5000 条）。 |
| **"只激活一个角色就不会出错" —— 这个规避方法不成立** | 之前没出错是因为**当时激活的是 momo**（6 个模块，计划只有 11.5 KB）。换成**单独一个伊卡洛斯**就已经约 **730 KB** 了：计划 327 KB + 提示词 78 KB + 运行时系统消息约 90 KB + 条目 235 KB。再加上防抖刷新会连着排队两条 `state`、engine.io 会把它们合并成一个请求 → 超过 1 MB → 同样卡死。**这是相关性，不是因果**；能不能出事取决于角色的计划有多大，不是激活了几个。 |
| 预算定太小会误伤用户看得见的正文（**比卡死还难受**） | 中途一版把预算设成 224 KB，真实数据（一个角色的全文上下文约 230 KB）一进来就触发截断，用户看到的就是"消息都显示不完整"。第八轮把预算提到 **448 KB** 并改成按预算装填 + 分页（见上）。**正文只在真正装不下时才被截。** |
| 「实时上下文」列表号称只给短预览，其实一直在推全文 | 一个**写进去但没人读**的死配置：`AddLiveContextSummary` 把 `Config.MaxPreviewChars` 设成 220，但全项目没有任何地方读它，真正取内容的是 `Limit()`，而它无条件返回全文。现在改成显式传参，并给条目打 `truncated` 标记（`MaxPreviewChars` 已标 `[Obsolete]`）。 |
| 截断后的预览有可能被当成正文写回，丢用户数据 | 后端 `ReadFullContent` 的最后兜底**不再返回预览**（宁可抛异常）；前端任何保存路径都先过一道闸：没取过全文就先 `item:load` 再让用户保存。 |
| 一次卡死只能重启 Alife | 出站线程加**自愈**：`inFlight` 阻塞超过 20s 就换一条新的发送线程（最多 2 次），并明确写出“请重启 Alife”。同时新增 `outbox REFUSED oversized` 这条明确日志。 |

### 第六轮（IPC 出站串行化 + 快照导入导出 + 记忆分层说明）

| 问题 | 现在 |
| --- | --- |
| 切换快照报「`charpreset:apply` 在 15 秒内没有收到后端响应」 | 这不是快照本身的问题：**后端的 IPC 桥卡死了**。现场日志里 `trace.log` 最后一行是 `-> state`，之后连 `window:close` 都没到后端 —— 说明后端在向窗口发送 `state` 报文时阻塞，而它当时**正握着 `stateLock`**，于是所有后续请求全部超时。第六轮先做了出站单线程队列 + 锁内构建锁外发送；**第七轮才找到真正的根因是报文体积**（见上）。 |
| 标题栏只有左边能拖动窗口 | `.titlebar` 改成**整条**都是拖动区（以前只有 270px 的 `.tb-left` 和居中的状态文字能拖；`.tb-center` 有 `justify-self:center`，1fr 列剩下的空白不属于任何拖动元素）。右侧按钮区用 `.no-drag` 排除。 |
| 「角色预设」按钮把「覆盖方式」那一栏撑得很宽 | 入口移到**导入栏**，和「酒馆预设 / 角色卡 / 世界书」并排，另加一个「导入角色记录」。 |
| 快照只能在插件里用，搬不出去 | 新增**导出 / 导入**：导出成 `.contextpreset.json`（一份文件 = 一个角色的整份上下文记录），导入到任意角色时自动改写成当前角色。界面上还写清了「一份快照到底存了什么」。 |
| 原始记忆流和 L1/L2 压缩层有什么区别、为什么只有压缩层能改 | 见下方「记忆分层」。两层**都能改**，但改的对象不同；现在每个盒子都写明了改的是哪里，且每行都有「编辑」按钮。顺带修掉一个静默失败：角色未激活或序号失效时，以前是无声无息不写入（看起来就是"改不了"），现在会明确报错。 |

### 第五轮（自动侧装 + 角色预设 + 系统消息全文可编辑）

| 问题 | 现在 |
| --- | --- |
| 每次启用插件覆盖都要手动打开窗口点「应用到当前角色」 | **自动侧装**：插件加载 / 角色激活时，只要该角色的计划是「插件覆盖」，就直接挂上请求前重组，并写 `applied.json`。第二次启动只启用本插件即可直接对话。 |
| 没有存放"这个角色的上下文记录"的地方 | 新增**角色预设**：每个角色一份自动快照 + 任意多份命名快照，可读取到编辑器或一键生效。 |
| 「预设」和角色卡/世界书混在一起分不清 | 统一改名为**酒馆预设**（存储目录仍是 `Presets`，兼容旧数据）。 |
| 系统消息改不了框架注入的"这是你的人物信息：名称/生日/简介/设定" | 「角色设定 #0」模块现在保存**完整官方系统消息全文**（含私人文件夹）。旧计划里只存裸 `Prompt` 的模块会自动升级。 |
| 插件覆盖时"预览是本地内容，实际发送的却是改过的" | 预览改为**按模式取内容**：插件覆盖显示计划内容（真正会发送的），关闭/本地覆盖显示运行时 `index[0]`；运行时 `index[0]` 另放在可展开的对照区。 |

## 使用

重载 `Alife.Function.Language.OpenAI` 和 `Marisa.ContextManager` 两个插件，重新打开上下文管理器。若旧窗口或旧实例没有更新，重启 Alife。无需手工更换 Runtime/CompiledPlugins 中的 DLL。

1. 选择角色，仅当前角色加载完整文本。切换角色会释放上一角色的前端上下文、世界书与计划；迟到的加载响应会被丢弃。当前角色的 `ChatHistoryEdited` 会触发防抖刷新，因此主聊天新收到的消息会同步到控制台。
2. 单击模块在右栏编辑；右栏和模块上的“编辑”按钮、双击模块都打开同一个大窗口。计划为空时才会补一个官方「角色设定 #0」模块；之后该模块内容为空就保持为空，不会被自动回填（「重置为空白」和 state 推送都不会让它复活）。
3. 每个大区域下有新增按钮。功能消息可以改输出身份、区域、区域内顺序和内容；插入会顺移后续模块，禁用模块不占发送位置。
4. **按区域 / 按来源批量清理**（每个区域标题右侧）：
   - `删除未勾选 (N)`：只删本区域内取消勾选的模块，勾选的不动。
   - `清空本区域`：删掉本区域全部模块（Alife 运行时消息不受影响）。
   - `重置为空白`（仅系统提示词区）：清空本区域所有模块，只保留一个**内容为空**的官方「角色设定 #0」，用来把导入过量的模块一次性清干净。当前是「本地覆盖」时会额外提示：重新应用后角色 `index.json` 的 `Prompt` 会被写成空内容。
   - 按来源视图下还有 `清空本来源`（例如把「酒馆预设」整块删掉）。
   - 清理后会自动保存草稿；若当前模式是「插件覆盖」或「本地覆盖」，会**一并重新应用**，让 `applied.json` / `index.json` 与界面一致。
5. 保存草稿不会改变正在运行的装配。选择“插件覆盖”后点击“应用”，整个计划一次提交；“预览”使用同一编译器。原生功能模块的编辑、身份调整、移动和禁用也需应用生效。对话历史默认勾选参与装配；取消后可使用对话区的 `删除未参与装配的对话` 清理未选的 user/assistant 消息。
6. 酒馆卡 JSON/PNG 在“酒馆兼容”导入；酒馆预设从下拉框选中后点击「酒馆预设」按钮导入；世界书可独立导入。导入后是可编辑的计划副本，修改资源库后需再次导入以更新。
7. 从“导入酒馆资源”创建的角色现在也会写一份 `TavernCard.json`，因此装配页的「角色卡」导入同样可用。

## 自动侧装（免手动"应用到当前角色"）

角色激活后（`ChatActivitySystem.Activated`，此时 `ChatBot.LanguageModel` 已就绪），插件会：

1. 读该角色的计划（`Storage/ContextManager/Plans/<角色>.json`）；计划文件不存在时先用**自动角色预设**恢复。
2. 计划是「插件覆盖」→ 写 `OverrideState/<角色>/applied.json`，并给该语言模型挂上请求前转换器。
3. 计划是「本地覆盖」→ 不需要侧装：`index.json` 的 `Prompt` 已经是最终内容，框架自己会用它构建 `index[0]`。
4. 计划是「关闭」→ 撤掉转换器，并清掉 `index.json` 里遗留的 `ContextManagerPlan`（否则 OpenAI 插件在没有转换器时会读它，"关闭"名不副实）。

三个触发点保证"只启用插件就能用"：插件构造（覆盖已激活的角色）、`Activated` 事件（之后激活的角色）、打开窗口时兜一次；另有 3 次 2.5 秒间隔的补试，应对极少数加载顺序问题。

请求前的转换器每次都会重新解析计划，优先级为 `applied.json` → 已保存的计划（仅当它是「插件覆盖」）→ `index.json` 里的遗留 `ContextManagerPlan`。所以即使 `applied.json` 被清理掉，插件覆盖仍会自动生效。

## 角色预设

每个角色在插件里保留一份上下文记录：

- `Storage/ContextManager/CharacterPresets/<角色>.json` —— **自动快照**，每次「应用」或「保存草稿」时自动更新。
- `Storage/ContextManager/CharacterPresets/<角色>__<名称>.json` —— 用户另存的**命名快照**，可存任意多份。

入口有三个：「上下文装配」**导入栏**里的 **角色预设** 按钮（弹窗）与 **导入角色记录**、以及左侧导航的 **角色预设** 页。每份记录可以：

- **读取到编辑器**：把该记录写回计划文件并刷新装配页，先检查再决定是否应用。
- **立即生效**：按记录里保存的覆盖方式直接应用（含自动侧装）。
- **导出**：写成 `.contextpreset.json`，默认落到桌面，文件名是 `<角色>.contextpreset.json`（自动槽）或 `<角色>__<名称>.contextpreset.json`（命名槽）。
- **删除**：仅命名快照可删；自动快照会随下一次应用重新生成。

### 快照里到底存了什么

一个文件 = **一个角色的一整份上下文记录**：

| 字段 | 含义 |
| --- | --- |
| `owner` | 这份记录原本属于哪个角色。导入到别的角色时会被改写成当前角色。 |
| `name` / `auto` | 快照名称；`auto=true` 表示是自动快照槽。 |
| `updatedAt` | 最后一次写入时间。 |
| `plan.mode` | `Off` 关闭 / `Temporary` 插件覆盖 / `Permanent` 本地覆盖。 |
| `plan.applyMacros` | 是否替换 `{{char}}`、`{{user}}` 等酒馆宏。 |
| `plan.userName` / `plan.customMacros` | `{{user}}` 的取值与自定义宏表。 |
| `plan.modules[]` | **全部模块**：`name / role / content（完整正文）/ enabled / group / source / constant / keywords`；框架注入的模块还带 `targetIndex / originalContent / originalRole`，用来还原原始消息。 |
| `sources.tavernCard` | **酒馆角色卡原文**（描述 / 性格 / 场景 / 开场白 / 作者注释等）。`{{description}}`、`{{char}}` 这些宏就是从这里取值的。 |
| `sources.worldBook` | **该角色的世界书原文**（条目、关键词、常驻标记、注入顺序）。 |
| `sources.preset` + `sources.activePreset` | 抓取时选中的**酒馆预设原文**及其名称。 |
| `sources.fingerprints` | 抓取时各源文件的「长度 + 最后写入时间」指纹。自动快照每次保存方案都会被调用，指纹没变就直接复用上一次的结果 —— 否则每勾选一个模块都要重新解析一份 1 MB 的角色卡。 |

**为什么要把设定原文也存进来**：模块正文里大多是 `{{description}}`、`{{worldbook}}` 这类**宏**，取值来自角色目录里的 `TavernCard.json` / `WorldBook.json` 和 `Presets/<名>.json`。只存「配方」不存「原料」，换角色或换机器后宏会展开成空 —— 所以现在两者一起打包，一份文件就是完整的一套设定。

**仍然不含**：聊天记录、记忆归档、头像图片本体。

**回填规则**：读取 / 立即生效 / 导入 / 计划文件丢失自动恢复时，都会把 `sources` 里的文件写回角色目录，但**只补缺失的文件**：角色目录里已有的 `WorldBook.json` / `TavernCard.json` 不会被覆盖（那是你本机可能改过的），真要覆盖时先落一份 `.backup-<时间戳>`。酒馆预设也只在「当前没选中任何预设」时才替你选上，不会改掉你手选的那个。

**兼容**：没有 `sources` 字段的旧快照（第八轮之前导出的）照常导入，只是不会带回设定来源。

### 导入规则

- 导入时 `owner` 一律改写成当前选中的角色，所以可以拿 A 角色的方案去搭 B 角色。
- 导入的落点是**命名快照槽**。如果文件里的名字正好等于角色名（会被当成自动快照槽），会自动改名为 `<角色>（导入）`，避免悄悄覆盖那份自动维护的记录。
- 也接受**裸计划文件**（直接把 `Plans/<角色>.json` 拿来导入）。
- 导入进来的 `sources` 会先落到角色目录（只补缺失的文件），再存成快照；存快照时会**用导入进来的 `sources`**，不会用本机当前的 `WorldBook.json` / `TavernCard.json` 把它盖掉。
- 文件里 `plan.modules` 为空会明确报错。注意 `CharacterContextPreset.Plan` 有 `= new()` 初始化，所以「只判断 null」是拦不住的 —— 一个没有 `plan` 字段的文件会被反序列化成"0 个模块的空计划"，导入后直接顶掉你的装配方案。这个坑已经按模块数量判断堵上（见单元测试 `importing a file without a plan throws`）。

## 记忆分层：原始记忆流 vs L1/L2/L3

「长期记忆」页里的两个盒子是**两种不同的东西**，都可以改，但改的对象不一样：

| | 原始记忆流 | L1 / L2 / L3 压缩层 |
| --- | --- | --- |
| 数据来源 | 当前 `ChatBot.ChatHistory`（运行时内存） | 磁盘文件 `Memory/L<n>/*.txt` |
| 是什么 | 下一次请求会**原样发出去**的消息，序号就是它在本轮上下文里的位置 | MemoryService 写下的逐级压缩摘要；**不是**实时上下文，只有被取用后才会进入对话 |
| 编辑写到哪里 | 直接改写 ChatHistory 里那一条消息 | 直接改写那个 `.txt` 文件 |
| 会不会持久 | 随会话；MemoryService 之后可能重新压缩出新的摘要 | 会，文件就是最终产物 |
| 其它文件 | `Memory/History.json` 是逐条原始流水（离线可读） | `Memory/memory_index.duckdb` 是检索索引 |

以前编辑「原始记忆流」失败时是**静默 return**（角色没激活，或序号已经不存在），界面刷新后又变回旧内容，看起来就是"改不了"。现在这两种情况都会抛出明确原因。

## 两种模式

- 插件覆盖（临时）：草稿在 `Plans`；已应用版本在 `OverrideState/<角色>/applied.json`。不修改角色原始 Prompt，不修改 ChatHistory。重启后插件自动重新挂接转换器；切到「关闭」会撤掉转换器。请求前的替换只作用于**发送出去的副本**，实时历史本身不变，因此后续对话的增删改查与本地完全一致。
- 本地覆盖（永久）：角色 `index.json` 保存展开后的 Prompt，角色目录的 `ContextManagerOriginalPrompt.txt` 单独保留首次覆盖前的原文。“还原原始设定”恢复这份备份并重置计划。**写回时会剥离官方外壳**：如果「角色设定 #0」里是完整官方系统消息，只会把「设定」正文写进 `Prompt`，否则框架再套一层会出现两份人物信息。
- 关闭：不做任何注入，并清掉 `index.json` 里的遗留 `ContextManagerPlan`。

Handlebars 宏开关控制是否在预览、插件覆盖和本地覆盖时替换 `{{char}}`、`{{user}}`、`{{description}}` 等酒馆模板变量。开启时按当前角色资料展开；关闭时保留模板字面文本。
- 导入卡的完整原始字段另存角色目录 `TavernCard.json`，防止框架保存角色时丢字段。

## 发送顺序和身份

`index[0]` 是一个可替换的前置区域，不是一条能同时拥有三种身份的消息。编译器把系统区域中启用的酒馆预设、角色卡、世界书和自定义模块展开为真实 system/user/assistant 消息，再保留后面的功能模块、压缩记忆和对话。

「角色设定 #0」保存的是完整官方系统消息（名称 / 生日 / 简介 / 设定 / 私人文件夹），所以在插件覆盖下这段框架注入的内容也能整段改写。旧计划里只存裸 `Prompt` 的模块会在读取时自动升级；内容为空（主动重置为空白）或用户自定义过的内容不会被覆盖。

自定义功能模块放在原生功能消息之后；自定义记忆模块放在已有连续记忆摘要之后；对话区域自定义模块放在历史末尾。区域内按列表顺序发送。原生功能模块替换用原索引和原内容双重校验。

**装配永远不会打断对话。** 这个校验以前会抛异常，而转换器是在**每次请求前**执行的，所以一旦原生消息被框架改写，你在原生对话窗口每发一条消息都会报错。现在改成三级兜底：

1. 原生模块对不上 → 保留该条原文，记一条降级提示（预览弹窗可见，`trace.log` 记 `transform warn`）。
2. 计划/宏展开出错 → 整次转换放弃，按原始历史发送，`trace.log` 记 `transform FAILED`。
3. `OpenAILanguageModel.BuildRequest` 外面再包一层 try/catch，永久覆盖和临时覆盖都覆盖到。

OpenAI.BuildRequest 在序列化前对历史副本执行 ContextTransform。MemoryService 始终看到原始历史，因此不会存档或压缩预设中的 user/assistant 消息。没有接口的其他语言模型会在应用时明确提示不支持，不会假装成功。

OpenAI 侧的 ContextAssembly/ContextCompiler.cs、ContextPlanModels.cs 是同一编译器的命名空间隔离副本，更新时需同步。这避免插件类型/卸载相互依赖，且永久方案不依赖管理器存活。

> 本轮的改动**只在 `Marisa.ContextManager` 插件内**（`ContextManagerRuntime.cs` / `ContextAssemblyRuntime.cs` / `ContextPlanService.cs` / `ContextPlanModels.cs` / 新增 `ContextPromptText.cs` / 前端 `app.js`、`app.css`、`index.html`）。没有改动框架，也没有再改 OpenAI 插件——它只需要上一轮已经部署好的 `ContextTransform` 接口。

## 酒馆兼容范围

- JSON V1/V2/V3 常用卡字段、PNG chara/ccv3 文本块；保留原始卡数据。
- prompts 与 prompt_order 的顺序、enabled 和 role；常用角色字段 marker；独立 JSON 世界书和 character_book，关键词、常驻、次级关键词 AND、大小写匹配、插入顺序。
- {{char}}、{{user}}、description/personality/scenario/system/mesExamples 等常用宏，三花括号，嵌套 #if/#unless/else、trim、noop、setvar/getvar。`{{!注释}}` 与酒馆的 `{{//注释}}` 都按注释整段删除。
- **多条开场白**：`first_mes` 与 `alternate_greetings`（数组 / 对象 / 裸字符串三种形状都能认）全部读出来，编号成「开场白 / 开场白 2 / 开场白 3 …」。装配页导入角色卡时每条单独成一个模块，勾你想用的那条即可；「导入酒馆资源」时全部写成世界书条目。对应宏：`{{greeting}}`、`{{greeting2}}`、`{{greeting3}}`…，以及 `{{alternateGreetings}}`（额外开场用空行拼接）与 `{{greetingCount}}`。
- 世界书在每次请求时扫描最近 ScanDepth 条非 system 消息，默认 10。全部属于用户要求的前置区域，不模拟酒馆 depth/position 插入。
- 不等同于完整 SillyTavern 运行时：不执行扩展脚本、STscript、自定义 Handlebars helper、each/with、正则扩展、概率触发/递归扫描、CharX。开场白保留原始字段，不误作世界书条目每轮注入。
- **未识别的宏不再中断请求**：以前遇到没定义的宏（如酒馆预设里手写的 `{{//可以自己改…}}` 或提供方专有宏）会直接抛错，应用/预览看起来像"点了没反应"。现在这些宏**按原文保留**，后端在 `plan-applied`/`plan-preview` 里回报未识别清单，界面弹窗提示；可在"宏设置"里补定义，或关闭 Handlebars 宏替换按原文发送。
  - **「自动宏应用」（默认开）**：本插件没有酒馆那么丰富的宏能力（`lastPrompt`、`time`、`random` 这些要运行时状态）。勾上后，没实现也没定义的宏直接**填成宏名本身**（`{{lastPrompt}}` → `lastPrompt`，带参数的只取名字：`{{random::a::b}}` → `random`），预览里不再堆一长串「未识别的宏」。`{{char}}` 仍取角色名、`{{user}}` 仍取宏设置里填的名字。关掉就恢复上面那条「原样保留 + 回报」。入口在装配页控制栏与「宏设置」弹窗。结构错误（`#if` 未闭合、`else` 缺开始块等）仍会明确报错。
- **导入角色卡报“没有可导入的提示词模块”**：三种情况都给了明确说明，不再是一句笼统的报错。
  - 该角色根本没有 `TavernCard.json` → 提示先用「角色卡」按钮导入一张 JSON/PNG 卡。
  - 卡字段**已被预设中的 `{{description}}`/`{{personality}}` 等宏覆盖** → 这时会明确告诉你「无需重复导入」，并说明你在预览里看到的角色卡内容正是这些宏展开的结果；有部分字段被跳过时会弹窗列出是哪几个。
  - 卡字段**与「角色设定 #0」的内容逐字相同** → 同样说明「已包含在发送内容里」，并告诉你想单独编辑就先取消勾选「角色设定」分区里的模块。**不会**再为了「避免重复」而静默删掉「角色设定」分区。
- **「特殊卡」只适配基本信息**：可用字段是 8 个 —— 酒馆标准的 6 个（描述 / 性格 / 场景 / 系统指令 / 对话示例 / 历史后指令）加上**开场白**（含 `alternate_greetings` 里的额外开场）和**作者注释**。实测有些卡 6 个标准字段全空、整段设定都放在开场白里（`Sgw_2-gemini.png` 的 `first_mes` 有 32,051 字），这种卡以前会被直接拒绝导入。现在只要有任意一个字段有内容就会导入，并在提示里说明「已按基本信息导入」；只有 8 个字段全空且卡内没有世界书时才报「没有任何可用文本」。
- **卡内世界书**：不再用工具栏勾选框（用户没法预判某张卡里有没有世界书）。现在**读到卡、检测到 `character_book` 有条目就弹窗询问**，弹窗列出前几条条目标题做预览，按钮「一起导入 / 只要角色卡」，并带「记住我的选择」复选框。选择「一起导入」时会把 `data.character_book` 合并进角色目录的 `WorldBook.json` 并作为 `source=worldbook` 的模块加进装配。**判重按「标题 + 正文」**，用户自己加过的条目一律保留（导入是「加上去」，不是「换掉」）；合并前先落一份 `.backup-<时间戳>`。长期策略（`ask` / `always` / `never`）存在 `Storage/ContextManager/ImportOptions.json`，可视入口在**酒馆兼容页 → 导入选项**，随时能改回「每次询问」。装配页「角色卡」与酒馆兼容页「导入角色卡」共用同一套策略。
- **世界书形状统一**：酒馆用 `entries`（可以是对象）+ `comment/keys/secondary_keys/insertion_order/disable`，本插件的世界书面板读 `Entries`（数组）+ `Title/Keywords/InsertionOrder/Enabled`。以前把酒馆原文直接写进 `WorldBook.json`，面板读不到小写 `entries` → 「导入成功但一条都没有」。现在落盘前统一走 `TavernImport.NormalizeEntries`，读盘也走同一条路径（`GetWorldBook` 也改成规范化读取），两种来源都能正确显示。

参考：[酒馆宏](https://docs.sillytavern.app/usage/core-concepts/macros/)、[上下文模板](https://docs.sillytavern.app/usage/prompts/context-template/)、[角色卡 V2](https://github.com/malfoyslastname/character-card-spec-v2/blob/main/spec_v2.md)。

## 验证

- 管理器编译通过（0 错误；仅环境/平台警告）。
- 编译器 + 装配测试（`../context-manager-tests`，**298 项**）：第一条真实身份、插入顺序、禁用、记忆边界、关键词触发、模板、注释宏删除、未识别宏按原文保留并回报、预设顺序、保存读取、关闭模式；实际 OpenAI `BuildRequest` 的离线请求体（user/assistant、原历史不变，不发网络请求）；原生模块过期时降级为原文 + warning（不再抛错）；官方系统消息构造 / 本地覆盖剥离外壳 / 旧计划升级为官方全文 / 新建计划带官方全文 / 插件覆盖只换 index[0] 且其余消息顺序内容不变 / 角色预设 camelCase 往返；落盘缩进 vs 上线紧凑（并验证往返等价）/ 导入改写 owner / 名字等于角色名时避开自动快照槽 / 空名字回退 / 无 plan 与 0 模块必须报错 / 导出文件名 / 导出→导入完整往返（mode、userName、customMacros、keywords、targetIndex、originalContent、全文都不丢）；转义字节口径（ASCII 1 字节 / 汉字 6 字节 / `"` `\` 2 字节 / `\n` `\t` 短转义 / 其它控制字符 `\uXXXX` / `<&>` 也转义 / 代理对 12 字节 / 空串与 null 都是 2 字节）/ `Measure` 按转义字节而不是 UTF-8 / 预算内不降级 / 超预算先丢计划再丢提示词最后才截断正文 / 降级后必须真的落进预算 / 截断条目必须打 `truncated` 标记 / 小计划不因条目大而被白丢 / 400 条也能落进预算 / 3000 条按数量裁剪后落进预算且 `hiddenItems` 与实际相符 / 1·50·500·2000·5000 条都不超过预算且远低于硬上限 / 降级说明是人话；**第八轮新增：`FillToBudget` 装得下就给全文（一个字都不截）/ 装不下就只给元数据并打 `Truncated` / `kept + omitted` 恒等于输入条数（不静默丢条）/ 保持调用方的优先级顺序 / 实测结果真的落进预算（并断言「快照外壳」必须当作 reserve 传进去）/ 元数据档必须同时清掉 `Content` 和 `Attachments[].Detail`（否则降级是假的）/ 预算为 0 或负数时退回默认而不是什么都不装 / 保留量吃掉整个预算时全部延后而不抛异常 / 任意条数 × 任意预算下都落进预算；`FillTier` 让角色 Prompt（哪怕三个月前写的）排在最新聊天之前 / 同角色内部顺序与「当前查看谁」无关 / 分页两页拼起来 == 全集且不重不漏；**第九轮新增：角色卡可用字段清单（6 个标准字段 + 开场白 + 作者注释，且宏名互不重复）/ 世界书规范化（酒馆 `entries`(对象) + `comment/keys/secondary_keys/insertion_order/disable` → 本插件 `Entries`(数组) + `Title/Keywords/InsertionOrder/Enabled`，已有 `id` 必须保留、二次规范化必须幂等、`World()` 读规范化前后条目数一致）/ 合并卡内世界书（判重按「标题+正文」、用户已有条目原样保留且排在最前、新增追加在末尾、重复导入不再新增、空正文不写入、卡内无世界书时不动用户的 `WorldBook.json`）/ 快照 `sources` 落盘往返逐字不变（世界书条数、酒馆卡 `first_mes`、预设名与原文）/ 空 `sources` 判为空（不给每个快照塞空壳）/ 换主人时 `sources` 必须跟着走（漏掉就只剩配方，`{{worldbook}}` 展开成空）/ 第八轮之前导出的旧快照（无 `sources`）仍可导入**；**第十一轮新增：`EntryRows` 的形状守卫（收到已规范化的 JArray 必须认出而不是抛异常；认不出的形状返回空集；`null` 返回空集）/ `NormalizeEntries` 幂等（跑两遍条目数不变、已有 `Id` 必须保留、标题与正文不变）/ 崩溃现场复现（把规范化后的 JArray 直接喂给 `MergeWorldBook` 必须新增 2 条而不是抛 `ArgumentException`；同一份 JArray 再合并一次全部判重跳过；传原始酒馆书与传 JArray 的合并结果一致）/ `WorldBookFile` 也能吃 JArray / `EntryTitles`（弹窗预览用）读标题、遵守 `max` 上限、`null` 与无标题条目返回空**；**第十四轮新增：`MinKeepChars == 2000` / 复现现场（20 个 3 万字巨块 + 末尾一条 244 字）—— 那条 244 字**一个字都不截**、不被标 `truncated`，21 个模块一个不少且整份落进预算 / 被截的模块**只有一种长度**（水填平）且不低于 2000 / 30 条 2000 字正文在 200 万字巨块面前也全留、只砍巨块 / `FitText(244 汉字, 1000)` 返回**最长前缀**（166 字，旧档位只有 80）而不是往档位上凑 / 世界书面板里排在 10 条 4 万字长条目之后的 244 字条目同样不被截、只有 10 条长条目被截 / state 条目在 8 档预算（40K…8K）下 244 字都保留全文**；**第十五轮新增（16 条，门槛 vs 预算）：总量在预算内时门槛 0/2000/10000/20000/100000 都不截任何正文（门槛与「能否全显示」无关）/ 门槛 2000 时 1500 字短正文完整保留、门槛 10000 时**一条都保不住**（提高门槛反而更容易截到短正文）/ 产品门槛仍等于 2000 且必须让「保护短正文」档真的生效 / 总量超预算时门槛怎么调都压到同一个上限 / 448 KB ≈ 76,458 汉字、576 KB ≈ 98,304 汉字 / 预算 < 自设硬上限 < engine.io 的 1 MB 断连线（次序不能乱）**。
- 浏览器模拟 IPC 测试（`smoke-test.cjs`，Playwright + 桩 IPC，**44 组**：编号 1–28 及 28b/28e–28l 等子组）：通道取自窗口 URL、trace 落盘、模块卡片内容、插件覆盖下的预览取计划内容（原来的 bug）、填入官方模板、模式名、自动侧装提示、按来源分区、「酒馆预设」改名、区域/来源筛选、视图切换、按区域删除未勾选 / 重置为空白（含 state 推送后仍保持空白）/ 按来源清空、宏设置保存、导入被跳过字段的提示、预览的未识别宏与降级提示、角色预设页与弹窗的读取/生效/删除/保存、角色预设导出/导入入口、标题栏整条 drag 区域、长期记忆的分层说明与逐行编辑按钮、截断条目的保存拦截（必须先 `item:load`，不能把预览写回）/ 报文降级提示条（有降级才显示）/ 条目按数量裁剪时说明「只列出最近 N 条 + 还有 M 条未列出」；**第八轮新增：分页条按角色显示欠账（不能用全局合计）/ 点「加载更早」真的发 `items:page` 并带上 offset / 翻页后剩余条数按后端回传的 `remaining` 递减 / 全部加载完后按钮消失（不留死按钮）/ 截断且正文为空的条目必须显示「正文未随状态推送加载 · 读取全文」而不是留白 / 点它走 `item:load`；**出站合并不变量（读 `ContextIpcBridge.cs` 的 `Coalescible` 与页面的 `requestExpectations` 交叉比对：任何「某个请求的专属回复」都不允许被合并；`worldbook-state` 显式禁止）/ 一份 `state` 必须核销所有在等 state 的请求（广播回复）/ 非广播回复仍然一对一 / state 推送后不再重发 `character:load`·`worldbook:get`**；**第九轮新增：装配页头部必须讲清「插件覆盖 vs 本地覆盖」且点出「本地覆盖写的是角色 `index.json`」/ 头部不再出现 `CONTEXT RECIPE`、也不再重复一个 `h3` 标题 / 「带卡内世界书」默认勾选、取消勾选后 `withCardWorldbook=false` 必须随 `plan:import-file` 一起发出、重新勾选后为 `true` / 源码级不变量：角色卡导入不得再 `RemoveAll(Source=="native")`、必须走 `skippedByNative`、必须支持 `withCardWorldbook` 与 `MergeCardWorldBook`、必须用 `WorldBookFile` 规范化后再落盘 / 旧硬错误「没有可导入的提示词字段」必须消失、改为「没有任何可用文本」/ 快照卡片显示「含设定来源」、没抓到来源的明确写「只含装配配方」/ 快照说明块必须列出 `sources.tavernCard`·`sources.worldBook`·`sources.preset` / 导出弹窗列出随文件带走的设定来源 / 工具栏不再有「带卡内世界书」勾选框、`plan:import-file` 不得再带 `withCardWorldbook`（前端预判不了卡里有没有世界书） / 源码级不变量：`MergeCardWorldBook` 必须把原始 `cardBook` 传给 `MergeWorldBook`、不得出现 `NormalizeEntries(cardBook)`、必须有 `AskCardWorldbookAsync` + `MessageBoxOptions`、策略必须落到 `ImportOptions.json` / `EntryRows` 必须有形状守卫（`JArray direct`）、`MergeWorldBook` 形参必须叫 `incomingEntries`、必须有 `EntryTitles` / 酒馆兼容页「导入选项」默认「每次询问」、切换下拉框必须发 `import:options-save` 且说明文字跟着更新 / 角色卡导入完成弹窗必须说明卡内世界书怎么处理了 / 源码级不变量：世界书保存必须有 `StripShadowKeys` + `WorldBookShadowKeys`**、看门狗超时；**第十二轮新增：`ShrinkPlanForWire` 把 143 模块 / 9.9 万字的真实计划压进 448 KB 且**每个模块都还在**（id/name 完整）、缩水条数与被标记条数一致、每条都报全文长度 / 1 KB 极端预算下只缩正文不删模块 / `PackWorldBookRows` 首屏 40 条一条不少且整份落进预算 / 分页每页都是完整正文、不重不漏、`nextOffset` 一定前进（单条超整页也不会死循环）/ offset 语义精确 / 「装得下也要扣减预算」这条不变量（漏掉它缩水等于没做）；冒烟测试第 28h 组：预览标记含全文长度、完整模块不被误标、点「读取全文」只发一个 `plan:module`、预览编辑框 `readonly`、读回全文后恢复可写、读回后不被下一轮 `plan-state` 退回预览、世界书条目同样「标记 + 只读 + 读取全文」、面板顶部说明预览与未读回条数、「读取全部正文」从 offset 0 分页且读完后状态复位、第 28i 组：确认弹窗用的是插件自己的窄弹窗（不是原生对话框）、说明是哪张卡/多少条/预览标题/还有多少条/以后去哪里改策略、勾「记住我的选择」后回答 `accepted=true, remember=true`、关掉弹窗等价于「只要角色卡」且**同样回答后端**、无预览标题时不出空列表块、4 条源码级不变量（不得再出现 `Electron.Dialog.ShowMessageBoxAsync(`、必须有 `card-worldbook-ask`/`AskUiAsync`/`AnswerUiAsk`、必须处理窗口关闭、后端必须分发 `card-worldbook-answer`）；8 条源码级不变量（`readPlanFields` 跳过截断正文 / `keepLoadedModuleContent` / `continueWorldPaging` + `next > offset` / `worldEntryTruncated` / `ShrinkPlanForWire`+`PackWorldBookRows` 收在 `ContextStateBudget` / `else available -= cost` / `RestoreTruncatedModules` / 先 `SendPlan` 再 `plan-imported` / `PackWorldBookRows` 被 runtime 调用）；**第十三轮新增：`TavernImport.Greetings` 读出 `first_mes` + `alternate_greetings` 全集并按「开场白 / 开场白 2 / 开场白 3」编号、三种形状都能认、空串过滤、与首条重复的去掉、`GreetingMacro(1)=«{{greeting}}»`/`GreetingMacro(2)=«{{greeting2}}»`；`Environment` 暴露 `greeting2..N` / `alternateGreetings` / `greetingCount`；`Render` 的 `autoFillNames` 把未定义的宏填成宏名本身且带参数的只取名字（`{{random::a::b}}`→`random`）、不会盖掉真有取值的宏、不会复活注释宏、关掉后仍原样保留并回报；`ContextPlan.AutoMacros` 默认开、过 IPC 是 `autoMacros`、落盘（PascalCase）往返、老计划仍默认开；端到端编译出两种请求。冒烟测试第 28j 组：右栏搜索框在空态也常驻、搜模块名/正文/未参与装配标记、命中片段高亮、点结果选中模块 + 卡片 `.jump-flash`、跳转后清空搜索词、放大按钮隐藏四个前置字段且内容编辑器变高、再点还原、宏设置「自动宏应用」默认勾选 + 取消后随 `plan:save` 发回 `autoMacros=false`；5 条源码级不变量（`ContextPlan.AutoMacros = true` / `TavernImport` 读 `alternate_greetings` 且 `Greetings` 存在 / 导入角色卡走 `TavernImport.Greetings(card)` / `ContextCompiler` 有 `autoFillNames` 与 `greeting{n}` / 前端有 `moduleSearchMatches`·`jumpToModule`·`bindExpandEditor`）；**第十四轮新增第 28k 组：244 字的模块卡片不出现「预览 / 读取全文」徽标、真正超长的模块仍保留「预览 + 读取全文」、长正文的只读提示必须写明「超过 2000 字才会只下发预览」；4 条源码级不变量（`ContextStateBudget.MinKeepChars = 2000` / `FairCap`+`BinaryCap` 水填平 / 世界书分页 `NextOffset` 用 `start + list.Count` 不能用元数据游标 / 前端 `TRUNC_KEEP_CHARS = 2000`）；**第十五轮新增第 28l 组：自动后台拉全文必须**串行**（第一轮只发一个 `plan:module`，回包后才发下一个，`['a1'] → ['a1','a2'] → ['a1','a2','a4']`）、没被截的模块不进队列、状态栏显示「后台读取模块正文」进度、跑完一轮后计划里不再剩 `truncated`、装配页有「自动读取全文」开关、关掉后队列立刻停（不再发请求）、世界书首次下发有预览条目时自动从 offset 0 分页、内容全在预算内时不白跑一轮；4 条源码级不变量（`autoPlanFetching` 串行闸门 / `autoPlanTimer` 单条超时保护 / `resetAutoFetch` 能停队列 / `AUTO_FULLTEXT_KEY` 持久化开关）**）。
- 未在运行中的 Alife 进行真实模型聊天；需重载后用自己的角色验证端到端体验。以下功能依赖真实环境，请重点确认：**激活第二个角色不再卡死**、**消息正文显示完整**（不再出现"截断成 400 字"）、**不再出现 `outbox REFUSED oversized`**（`character-bundle` 不应再涨到 500 KB+）、**不再出现小请求莫名 15 秒超时**（`outbox coalesced` 里只应出现 `state`）、**分页条能正常翻页**（"加载更早的 40 条"点下去条目增加、剩余数递减、归零后按钮消失）、自动侧装（关掉窗口后直接对话）、角色预设的读取/生效/导出/导入、**导入角色卡后「角色设定」分区不再消失**、**勾选「带卡内世界书」后世界书面板能看到卡里的条目**、**`Sgw_2-gemini.png` 这类特殊卡能导入**、本地覆盖写回 `index.json` 后不出现重复人物信息、原始记忆流编辑是否按预期改写实时上下文、**`trace.log` 里 `state push: ... bytes=` 的实际数值**（应远小于 1 MB；激活第二个角色前后都要看，这是判断这次修复是否真的生效的最直接证据）。

## IPC 桥为什么会整程序卡死

这一节是第七轮的核心结论，读 `resources/app.asar` 得到的**确定事实**（不是推测）。

### 桥的结构

```
.NET  Electron.IpcMain.Send(window, channel, json)
  → socket.emit("sendToIpcRenderer", {window, channel, data})
     → Electron 主进程: BrowserWindow.fromId(id).webContents.send(channel, ...data)
```

- 这是**一条共享的 Socket.IO 连接**：所有窗口、所有 Electron API 调用都走它。
- Electron 侧服务端只覆盖了两个选项，**没有覆盖报文上限**，所以是 engine.io 默认的 1 MB：

```js
io = new Server({ pingTimeout: 60000, pingInterval: 10000 });   // maxHttpBufferSize 默认 1e6
```

- 超限时 engine.io 直接 `res.writeHead(413).end()` 并断开连接。
- **两端都没有重连逻辑**：Electron 侧 `global['electronsocket']` 只赋值一次、`disconnect` 分支里只有清理。所以**一次 413 就是永久卡死，只能重启 Alife**。

### 体积被严重低估：字符数 ≠ 网线字节数

`resources/bin` 下只有 `SocketIO.Serializer.SystemTextJson.dll`（没有 Newtonsoft 版），
说明 SocketIOClient 用 `System.Text.Json` 序列化，它的默认 encoder 会把**所有非 ASCII
写成 `\uXXXX`** —— 一个汉字 6 字节。用真实数据重建那条报文实测：

| 序列化方式 | 字节数 |
| --- | --- |
| 中文不转义 | ≈ 500 KB |
| 中文转义成 `\uXXXX`（真实情况） | **≈ 850 KB** |

（重建值 214,748 字符 vs 现场日志 216,535 字符，吻合度 99.2%。）

所以预算必须按**转义后**的字节数算。`ContextStateBudget.EscapedBytes` 逐字符精确计算
（ASCII 1 字节、`"`/`\` 2 字节、控制字符与所有非 ASCII 6 字节），预算、硬上限、计划内联
阈值、限速窗口全部走这一个口径 —— 之前用 UTF-8 字节数时，名义 512 KB 的硬上限实际会放行
约 920 KB，等于没有保护。

### 触发链（2026-09-29 19:01:04 现场）

1. 激活第二个角色 → 后端给**每个已激活角色**都内联一份计划 + 提示词，
   `state` 从 13,235 字符涨到 216,535 字符（≈850 KB，1 MB 上限的 85%）。
2. `OnActivityActivated` → `EditChatHistory` → `ChatHistoryEdited` →
   `ScheduleHistoryRefresh()`（350ms 定时器）→ `QueueInitialState()`，**会连着排队多条 state**。
3. Engine.IO 的 **polling 传输会把「上一次 POST 还没回来时」产生的多个数据包合并成
   一个 HTTP 请求体** → 两条 850 KB 拼成 1.7 MB → **413 断连**。
4. 桥死 → 所有 Electron API 调用停摆 → 主窗口的 Blazor 电路心跳失败 →
   弹出 `Rejoin failed` 遮罩 → 用户看到"程序卡死"。

> 关于 `Rejoin failed... trying again in 1 second`：它**不是日志**，而是 Blazor 重连遮罩的
> UI 文案（`resources/bin/wwwroot/_framework/blazor.server.js` 里的
> `this.status.innerHTML = ...`）。**插件窗口没有 Blazor**，所以看到它说明整个进程已停摆。

### 现在的八层防护

| 层 | 做法 |
| --- | --- |
| 1. 只发当前角色 | `state` 里 `planJsonByOwner` / `characterPromptByOwner` / `runtimeSystemByOwner` **只给渲染进程正在查看的那个角色**（前端本来就只读这几个字段的当前角色那份，而且缺计划时会走 `plan:get` 兜底）。 |
| 2. 大计划不内联 | 单份计划 JSON 超过 48 KB（转义后）就不内联进 `state`，改由 `plan:get` 单条下发（日志：`plan inline skipped ...`）。计划本身超过 448 KB 时直接回 `plan-error` 说明原因，而不是让桥静默拦截。 |
| 3. **按预算装填（`FillToBudget`）** | 不再假设"全部条目都塞得进一条报文"。逐条累加：放得下正文就给**全文**，放不下就只给元数据并打 `Truncated`（界面显示「读取全文」），连元数据都放不下就计入 `omitted` 并提示「还有 N 条未加载」，点一下走 `items:page`。`state` / `character-bundle` / `items:page` 三处共用同一个 `OrderForFill`，保证分页 offset 对得上。 |
| 4. 装填分档 | `FillTier`：`character-prompt` / `offline-system` / `live-system` / `global-config` 为第 0 档，永远先进报文。否则"三个月没改角色设定"会让角色 Prompt 被新聊天挤出报文，提示词工作室直接空白。 |
| 5. 真实转义字节预算 + 降级阶梯 | `ContextStateBudget`：预算 **448 KB（转义后）**，按 `丢计划 → 丢提示词 → 正文 4000/2000/800/400/220/80/0 字 → 按数量裁剪` 逐级降级，并在 `state.degradeSteps` / `state.hiddenItems` / `state.omittedByOwner` 里告诉界面丢过什么。**正文只在真实数据装不下时才被截**（见下）。 |
| 6. 出站限速 + 合并 | 600ms 内累计不超过 512 KB、两条间隔 ≥25ms。**合并只允许 `state`**：它是唯一「最新一份即完整状态、丢旧的无损」的类型；其余类型（`worldbook-state` / `presets-list` / `charpreset-list` / `plan-state` / `native-prompt`）都是**某个请求的专属回复**，合并它们会让 N 个请求只收到 1 条回复 → 前端 15 秒后误报超时。冒烟测试会读 `ContextIpcBridge.cs` 的 `Coalescible` 声明与页面里的 `requestExpectations` 交叉比对，任一边加错类型都会立刻失败。 |
| 7. 单条硬上限 | 超过 **576 KB（转义后）** 一律不发，记 `outbox REFUSED oversized`，并回一条小报文让界面说明原因。真值放在 `ContextStateBudget.MaxWireBytes`，和预算共用同一套口径。它是**兜底网**：正常路径都走 `FillToBudget`，天然不超过预算。 |
| 8. 截断保护 + 自愈 | 截断条目打 `truncated` 标记，前端保存前必须先 `item:load`；出站线程阻塞超 20s 自动换新线程（最多 2 次）。 |

### 预算为什么是 448 KB：别把用户看得见的内容截掉

预算**不是越小越安全**。定小了会让用户直接看到"消息显示不完整"，那比卡死还难受
（2026-09-30 那版设成 224 KB 就犯了这个错，真实数据一进来就触发截断）。

把现场那份 850 KB 的报文拆开，真实构成是：

| 部分 | 转义后字节 | 现在怎么处理 |
| --- | --- | --- |
| 装配计划 JSON（伊卡洛斯 33 个模块） | 327 KB | 不内联，走 `plan:get` 按需下发 |
| 角色提示词 + 运行时系统消息 | ≈ 175 KB | 只给当前角色，超预算就丢，前端读 `index.json` / `native-prompt:get` |
| **条目正文**（90 条实时消息全文） | **138 KB** | **原文发送，不截** |
| 归档文件预览（46 个，本来就只发 180 字） | 30 KB | 原文发送（这是原本的设计） |
| 141 条条目的 JSON 字段开销 | ≈ 63 KB | 无法省，只能靠分页/数量裁剪兜底 |

也就是说：**真正的大头是计划和提示词，不是正文**。这两块改成按需读取之后，
一个角色的全文上下文合计约 **230 KB** —— 预算取 448 KB 就能原文装下，
还留 49% 余量。单元测试里有一条专门的回归断言，用这个真实形状（141 条、全文正文）
断言 `!Degraded` 且没有任何一条被打上 `truncated`。

但**余量不是方案**：真实数据是会长大的（星野雨一天就有 114 条实时消息 + 83 个归档，
光元数据 190 KB、正文 219 KB；用一个月就是几千条）。所以第八轮的正解是
**按预算装填 + 分页** —— 「一条报文最多多大」由预算硬保证，与用户用了多久无关。
预算取 448 KB 是因为它同时满足两个条件：装得下"一个角色一天"的完整上下文（不误伤正文），
又只有 1 MB 断连线的 44%。

安全边界也核对过：单条报文上限 576 KB、600ms 窗口内总量上限 512 KB，
即使 engine.io 把窗口内的报文合并成一个请求，也远不到 1 MB 的断连线。

## 运行时诊断（按钮点了没反应时先看这里）

界面出现"正在保存 / 正在应用 / 正在选择"后长时间没有结果时，看两个日志文件：

- `Storage/ContextManager/trace.log`：后端收到/发出的每条 IPC，以及计划服务目录、角色、模块数量、`auto attach`、`native modules upgraded`、`character preset` 读写。
- `Storage/ContextManager/renderer-trace.log`：渲染进程实际加载的 app.js 构建号（`APP_BUILD`）、窗口 URL、IPC 通道、每条请求与回复。

判断顺序：

1. `renderer-trace.log` 里没有 `boot build=...`：窗口加载的 app.js 没跑起来，检查 `Resources/ContextManager/index.html` 的脚本标签（不要用 `document.write` 注入外链脚本）。
2. 有 `-> xxx` 但 `trace.log` 里没有 `<- xxx`：渲染进程到后端的 IPC 没到达，通常是插件没重载（Alife 仍在跑旧编译产物）或窗口用的是旧缓存页面。
3. `trace.log` 有 `<- xxx` 却没有对应的 `-> xxx` 回复：后端处理抛异常，日志里会出现 `!! handle` 或 `plan operation FAILED`。
3b. **请求到了、后端也回了，但前端还是报「15 秒没有收到响应」**：回包在**出站队列**里被吃掉了。2026-09-29 现场就是这一类 —— `<- worldbook:get` 有两次、`-> worldbook-state` 只有一次（两次回包在队列里撞在一起被合并）。查两条日志：`outbox backlog queue=N`（队列堆积）与 `outbox coalesced type=...`（真的发生了合并）。
   - 只有 `state` 允许被合并；**出现别的类型就是 bug**，需要把它从 `ContextIpcBridge.Coalescible` 里去掉。
   - 队列堆积的常见诱因是「每次 `state` 推送都重拉一遍 300+ KB 的 `character-bundle`」。判据：`state push:` 之后紧跟 `<- character:load` → `-> character-bundle bytes=300000+`。如果 state 里已经带了该角色的条目，就不该再重拉。
4. **`trace.log` 整个停止更新（连 `<- xxx` 都没有）**：IPC 桥断了/卡死了，不是插件逻辑问题。2026-09-29 现场就是这样 —— 最后一行是 `-> state`，之后连 `window:close` 都没到后端。现在有四条线索可以确认：
   - `-> state bytes=N` —— 每条出站报文的**真实转义后字节数**（不是字符数，也不是 UTF-8 字节数；汉字算 6 字节）。N 接近 1 MB 就危险。
   - `state push: ... bytes=N budget=M steps=[...] omitted=K` —— 快照降级后的字节数与降级步骤；`steps` 非空说明有大字段被改成按需读取，`items-trimmed-K` 表示条目过多只列出了 K 条；`omitted=K` 是本次没进报文、需要走分页的条目数（**这是常态，不是异常**）。
   - `state degraded: ...` —— 上一条的降级步骤翻成人话。
   - `character-bundle owner=X total=N sent=M omitted=K inactive=?` —— 按需加载一个角色的结果。`sent < total` 是正常的（其余走 `items:page`）；`inactive=true` 表示该角色未激活、按策略不读本地上下文。
   - `items:page owner=X offset=N total=T sent=S remaining=R` —— 一次分页请求。`remaining` 归零后界面上的「加载更早」按钮会消失；如果 `sent=0` 而 `remaining>0`，说明这一页被预算压空了，前端会强制归零避免死按钮。
   - `outbox REFUSED oversized type=... bytes=...` —— 有一条报文超过 576 KB 硬上限，已被阻止发送（宁可丢这条，也不让桥被 413 断开）。正常路径都走 `FillToBudget`，不该看到这条。
   - `outbox backlog queue=N type=...` —— 出站队列堆到 24 条以上。限速在正常工作，但持续堆积会把**小回包**推迟到前端 15 秒看门狗之后。它和下面的 `coalesced` 一起，是「小请求莫名超时」的第一嫌疑人。
   - `outbox coalesced type=state total=N queue=M` —— 队列里尚未发出的同类型旧报文被最新一份取代。只有 `state` 允许出现这条；**如果这里出现别的类型，就是 bug**（那条请求的回包被吃掉了）。
   - `outbox STALLED '<type>' 已阻塞 Ns` / `outbox 自愈：...` —— 出站线程卡住并已尝试换新线程；自愈到上限后只能重启 Alife。
5. 后端 `runtime created build=...` 里的 `assembly/built` 说明 Alife 实际加载的是哪一份 DLL。
6. 自动侧装是否生效：搜 `auto attach owner=<角色>`，`attached=True` 表示转换器已挂上；`mode=Off` 表示该角色不是插件覆盖，属正常跳过。
7. 激活流程的分点日志：`activate begin owner=X activeBefore=N` → `activate done owner=X activeAfter=M` → `state push: ...`。如果只有 `begin` 没有 `done`，卡在框架的 `chatActivitySystem.Activate`；如果 `done` 之后卡住，看 `-> state` 的 `bytes=`。
8. 快照体积异常时先看这两条：`plan ok owner=X modules=N` / `plan inline skipped owner=X planBytes=N`。后者说明该角色的计划太大、改走 `plan:get` 了。

后端 `AppBuild` 常量必须与 `app.js` 里的 `APP_BUILD` 一致；不一致时界面会直接提示"前端为旧版本"。

注意：插件目录里不要放本地 msbuild 产物（`*.dll`、`bin/`、`obj/`）。框架 `RequirePluginDll` 会把插件目录下所有 `*.dll` 一起加载，旧程序集会与新编译产物里的同名类型冲突。

