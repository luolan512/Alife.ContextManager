using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ElectronNET.API;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace Marisa.ContextManager;

public sealed class ContextIpcBridge : IDisposable
{
    public event Action<string, JsonElement>? OnMessage;
    // Each window/runtime gets its own IPC channel. RemoveAllListeners is global
    // for a channel, so a stale plugin instance must never unregister a newer one.
    public string ChannelId { get; } = "marisa-context-manager-" + Guid.NewGuid().ToString("N");
    BrowserWindow? window;
    readonly ILogger logger;
    static readonly JsonSerializerSettings JsonSettings = new()
    {
        ContractResolver = new CamelCasePropertyNamesContractResolver()
    };

    // =====================================================================
    // 出站通道（2026-09-30 第二版）
    //
    // 事实链（全部有现场证据，见 README「IPC 桥为什么会整程序卡死」）：
    //   1. Electron.NET 的桥是「一条 Socket.IO 连接 + Electron 主进程回调」，
    //      所有窗口、所有 Electron API 调用共用它；Electron 侧
    //      `global['electronsocket']` 只赋值一次，disconnect 分支里只有清理，
    //      **两端都没有任何重连逻辑** —— 连接一断就是永久死亡，只能重启 Alife。
    //   2. Electron 侧服务端是 `io = new Server({pingTimeout:60000,pingInterval:10000})`，
    //      没有覆盖 engine.io 的默认 `maxHttpBufferSize: 1e6`（1 MB）。
    //      超限时 engine.io 直接 `413 Payload Too Large` 并断开连接。
    //   3. Engine.IO 的 polling 传输会把「上一次 POST 还没回来时」产生的多个数据包
    //      合并成一个 HTTP 请求体。所以危险的不是单条报文，而是**连着发几条大报文**。
    //   4. 2026-09-29 现场：激活第二个角色时 `state` 变成 216,535 字符
    //      （约 500 KB 未转义 / 850 KB 转义，见下），随后后端整个停止响应，
    //      主窗口 Blazor 电路掉线并弹出 `Rejoin failed... trying again in 1 second`。
    //
    // 注意「转义」：SocketIOClient 用的是 System.Text.Json 序列化器
    // （resources/bin 下只有 SocketIO.Serializer.SystemTextJson.dll，没有 Newtonsoft 版），
    // 它的默认 JavaScriptEncoder 会把所有非 ASCII 字符写成 \uXXXX（6 字节/汉字）。
    // 所以一条「500 KB 中文」的报文，真正写到 socket 上是约 850 KB。
    //
    // 于是这里做四件事：
    //   A. 单条报文硬上限 MaxWireBytes：超了宁可丢，也绝不交给 Electron.NET；
    //   B. 出站限速：窗口内总字节数受限 + 两条之间留最小间隔，杜绝「攒成一个巨包」；
    //   C. 合并（coalesce）：state 这类「最新一份就是完整状态」的消息，队列里只留最新一条；
    //   D. 卡死自愈：发送线程被卡住时换一条新线程，避免一条坏消息永久堵死出站方向。
    // =====================================================================

    /// <summary>
    /// 单条报文的硬上限。超过它一律不发，避免触发 Socket.IO 的 413 断连。
    ///
    /// 口径是**转义后的字节数**（ContextStateBudget.EscapedBytes），也就是真正写进
    /// WebSocket 帧的字节数。以前这里用的是 UTF-8 字节数，等于把上限悄悄放大了约 1.8 倍
    /// —— 名义 512 KB、实际约 920 KB，正好贴着 1 MB 的断连线，保护形同虚设。
    /// 真值放在 ContextStateBudget，和 state 预算共用同一套口径。
    /// </summary>
    public const int MaxWireBytes = ContextStateBudget.MaxWireBytes;

    // 流量窗口：WindowMs 内累计出站字节不超过 WindowBytes，且两条之间至少 MinGapMs。
    // WindowBytes 取 512 KB（不到 1 MB 的一半）：即使 engine.io 把窗口内的报文合并成一个
    // 请求，也远不到 413 的阈值。注意单个大报文（例如 plan-state 约 320 KB）在窗口为空时
    // 仍然放行 —— 判断条件是「窗口内已有流量」时才等，所以不会把单条大报文饿死。
    const int WindowMs = 600;
    const int WindowBytes = 512 * 1024;
    const int MinGapMs = 25;

    // ⚠️ 只有「后一条完全取代前一条」的**幂等全量状态推送**才能合并。
    //
    // 判据不是「名字看起来像状态」，而是**前端有没有在为它等一条回包**：
    // app.js 的 requestExpectations 里，worldbook-state / presets-list / charpreset-list /
    // plan-state / native-prompt **全都是某个请求的回复**，而 noteReply 的语义是
    // 「一条回复核销一个待回请求」。一旦合并，N 个请求只会收到 1 条回复，
    // 剩下的会在 15 秒后弹出「在 15 秒内没有收到后端响应」。
    //
    // 2026-09-29 现场就是这么炸的：出站队列被 354 KB 的 state 和 329 KB 的
    // character-bundle 顶住（限速 512 KB/600ms 时，一个 state+bundle 周期就要 1.4s），
    // 两次 worldbook:get 的回包在队列里撞在一起被合并成一条 → 前端超时。
    // trace.log 里能看到 `<- worldbook:get` 有两次、`-> worldbook-state` 只有一次。
    //
    // 只有 state 满足「最新一份即完整状态、丢旧的无损」，所以只留它。
    static readonly string[] Coalescible = { "state" };

    // 队列开始变长说明出站速度跟不上推送速度。这不是错误（限速在正常工作），
    // 但它是「小回包被大报文挤到后面、前端开始超时」的前兆，所以要在日志里留痕。
    const int BacklogWarnDepth = 24;

    const int OutboxCapacity = 256;

    readonly object queueGate = new();
    readonly LinkedList<Outgoing> queue = new();
    readonly Dictionary<string, LinkedListNode<Outgoing>> coalesceIndex = new(StringComparer.Ordinal);
    readonly SemaphoreSlim queueSignal = new(0, int.MaxValue);

    readonly object windowGate = new();
    readonly Queue<long> windowTicks = new();
    readonly Queue<long> windowSizes = new();
    long lastSendTicks;

    readonly object senderGate = new();
    Thread? sender;
    Timer? watchdog;
    volatile bool disposed;
    long inFlightSince;
    string inFlight = "";
    int stalledReported;
    int recoveries;
    long dropped;
    long refused;
    long coalesced;
    int backlogWarned;
    long sentCount;
    long sentBytes;

    sealed record Outgoing(string Type, object? Payload);

    public ContextIpcBridge(ILogger logger)
    {
        this.logger = logger;
    }

    public Task RegisterAsync() => Electron.IpcMain.On(ChannelId, OnIpcMessage);

    public void SetWindow(BrowserWindow? value) => window = value;

    static bool IsCoalescible(string type)
    {
        foreach (var candidate in Coalescible)
            if (string.Equals(candidate, type, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>
    /// 非阻塞入队。真正写 socket 的动作由唯一的后台线程完成，并且带限速。
    /// </summary>
    public void Send(string type, object? payload = null)
    {
        if (disposed || window == null) return;
        try
        {
            EnsureSender();
            var item = new Outgoing(type, payload);
            var signal = true;
            lock (queueGate)
            {
                if (IsCoalescible(type) && coalesceIndex.TryGetValue(type, out var existing) && existing.List != null)
                {
                    // 用新的顶掉还没发出去的旧消息：`state` 是完整快照，旧的那份没有任何价值，
                    // 而两条大报文挤在一起正是 413 的成因。
                    var replacement = queue.AddAfter(existing, item);
                    queue.Remove(existing);
                    coalesceIndex[type] = replacement;
                    signal = false;
                    // 合并本身是无损的，但它会让队列变短、掩盖堆积，所以记一笔 ——
                    // 出问题时能直接从 trace.log 看出「到底发生过什么」，不用再猜。
                    var merged = Interlocked.Increment(ref coalesced);
                    if (merged == 1 || merged % 20 == 0)
                        ContextTrace.Write($"outbox coalesced type={type} total={merged} queue={queue.Count}（队列里尚未发出的同类型旧报文被最新一份取代）");
                }
                else if (queue.Count >= OutboxCapacity)
                {
                    // 队列满说明出站方向已经堵住；丢新消息而不是无限堆积内存。
                    var total = Interlocked.Increment(ref dropped);
                    if (total == 1 || total % 50 == 0)
                        ContextTrace.Write($"outbox overflow dropped={total} type={type}（后端已经无法向窗口写入数据）");
                    return;
                }
                else
                {
                    var node = queue.AddLast(item);
                    if (IsCoalescible(type)) coalesceIndex[type] = node;
                    if (queue.Count >= BacklogWarnDepth && Interlocked.Exchange(ref backlogWarned, 1) == 0)
                        ContextTrace.Write($"outbox backlog queue={queue.Count} type={type}（出站队列堆积：限速在起作用，但持续堆积会把小回包推迟到 15 秒看门狗之后）");
                }
            }
            if (signal) queueSignal.Release();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "ContextManager 出站入队失败");
        }
    }

    void EnsureSender()
    {
        if (sender != null) return;
        lock (senderGate)
        {
            if (sender != null) return;
            StartSenderLocked();
            watchdog = new Timer(_ => CheckStalled(), null, 5000, 5000);
        }
    }

    void StartSenderLocked()
    {
        sender = new Thread(SenderLoop) { IsBackground = true, Name = "ContextManager-IPC-Sender" };
        sender.Start();
    }

    Outgoing? TakeNext()
    {
        lock (queueGate)
        {
            var first = queue.First;
            if (first == null) return null;
            var item = first.Value;
            if (IsCoalescible(item.Type) && coalesceIndex.TryGetValue(item.Type, out var node) && node == first)
                coalesceIndex.Remove(item.Type);
            queue.RemoveFirst();
            // 队列排空到警戒线一半以下时重新武装堆积告警，否则「持续堆积」和
            // 「偶发一次堆积」在日志里长得一样，分不出来。
            if (queue.Count < BacklogWarnDepth / 2) backlogWarned = 0;
            return item;
        }
    }

    void SenderLoop()
    {
        try
        {
            while (!disposed)
            {
                // 等一个入队信号；超时只是为了定期醒来检查 disposed。
                if (!queueSignal.Wait(500))
                {
                    if (disposed) break;
                    continue;
                }
                var item = TakeNext();
                if (item == null) continue;
                try { SendNow(item.Type, item.Payload); }
                catch (Exception ex) { logger.LogError(ex, "ContextManager 发送渲染进程消息失败 {Type}", item.Type); }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "ContextManager 出站线程退出");
        }
    }

    // 卡死检测：Send 是同步阻塞的，无法取消。至少让日志能说清发生了什么，
    // 而不是让用户只看到「15 秒没有收到后端响应」。
    void CheckStalled()
    {
        var type = inFlight;
        if (string.IsNullOrEmpty(type)) { stalledReported = 0; return; }
        var seconds = (Environment.TickCount64 - Interlocked.Read(ref inFlightSince)) / 1000;
        if (seconds < 10) return;
        if (Interlocked.Exchange(ref stalledReported, 1) == 1) return;
        ContextTrace.Write(
            $"outbox STALLED '{type}' 已阻塞 {seconds}s：Electron.NET 的 IPC 桥可能已经卡死。" +
            "之后渲染进程发出的任何请求都不会有响应（Electron 侧没有重连逻辑，通常需要重启 Alife）。" +
            $"本轮出站统计：sent={Interlocked.Read(ref sentCount)} bytes={Interlocked.Read(ref sentBytes)} refused={Interlocked.Read(ref refused)}");
        if (seconds >= 20) RecoverStalled(type);
    }

    // 自愈：卡住的线程是后台线程，救不回来；但可以换一条新的发送线程，
    // 让后续消息不再排在它后面。若桥本身已经断了，新线程的第一条也会卡住，
    // 所以这里限制次数，避免无限造线程。
    void RecoverStalled(string type)
    {
        if (Interlocked.Increment(ref recoveries) > 2)
        {
            ContextTrace.Write("outbox 自愈已达上限（2 次）：Electron.NET 的桥大概率已经断开，请重启 Alife。");
            return;
        }
        try
        {
            lock (senderGate) { StartSenderLocked(); }
            inFlight = "";
            stalledReported = 0;
            ContextTrace.Write($"outbox 自愈：'{type}' 之后启用新的出站线程（第 {recoveries} 次）");
        }
        catch (Exception ex)
        {
            ContextTrace.Write("outbox 自愈失败: " + ex.Message);
        }
    }

    void SendNow(string type, object? payload)
    {
        var target = window;
        if (target == null) return;

        string json;
        long bytes;
        try
        {
            JObject envelope = new() { ["type"] = type };
            if (payload != null)
            {
                JObject payloadObject = JObject.FromObject(payload, Newtonsoft.Json.JsonSerializer.Create(JsonSettings));
                foreach (var pair in payloadObject.Properties())
                    envelope[pair.Name] = pair.Value;
            }
            json = envelope.ToString(Formatting.None);
            // 用「转义后」的字节数：Electron.NET 还会把这条字符串再序列化一次，
            // 而 System.Text.Json 会把每个汉字写成 \uXXXX。按 UTF-8 算会低估近一倍。
            bytes = ContextStateBudget.EscapedBytes(json);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "ContextManager 序列化出站报文失败 {Type}", type);
            return;
        }

        if (bytes > MaxWireBytes)
        {
            var total = Interlocked.Increment(ref refused);
            ContextTrace.Write(
                $"outbox REFUSED oversized type={type} bytes={bytes} limit={MaxWireBytes} total={total}" +
                "（已阻止发送：Socket.IO 的 maxHttpBufferSize 是 1 MB，超限会被 413 断开且不会重连）");
            // 不能递归调用 Send：这里直接构造一条小报文，让界面知道发生了什么。
            if (type != "error")
                EmitSmall(target, "error",
                    $"一条「{type}」数据有 {bytes / 1024} KB（转义后），超过安全上限 {MaxWireBytes / 1024} KB，" +
                    "已阻止发送以免把 IPC 桥卡死。请减少该角色参与装配的上下文条目后重试。");
            return;
        }

        Throttle(bytes);

        Interlocked.Increment(ref sentCount);
        Interlocked.Add(ref sentBytes, bytes);
        inFlight = type;
        Interlocked.Exchange(ref inFlightSince, Environment.TickCount64);
        try
        {
            ContextTrace.Write($"-> {type} bytes={bytes}");
            Electron.IpcMain.Send(target, ChannelId, json);
        }
        finally
        {
            inFlight = "";
        }
    }

    // 限速：保证「最近 WindowMs 内的出站字节 + 本条 <= WindowBytes」，且两条之间 >= MinGapMs。
    // 目的是让每条报文各自占一个 HTTP 请求，而不是被 engine.io 攒成一个 >1 MB 的巨包。
    void Throttle(long bytes)
    {
        while (true)
        {
            long waitMs;
            lock (windowGate)
            {
                var now = Environment.TickCount64;
                while (windowTicks.Count > 0 && now - windowTicks.Peek() > WindowMs)
                {
                    windowTicks.Dequeue();
                    windowSizes.Dequeue();
                }
                long used = 0;
                foreach (var size in windowSizes) used += size;
                var sinceLast = lastSendTicks == 0 ? long.MaxValue : now - lastSendTicks;
                if (sinceLast < MinGapMs)
                {
                    waitMs = MinGapMs - sinceLast;
                }
                else if (windowTicks.Count > 0 && used + bytes > WindowBytes)
                {
                    waitMs = Math.Max(1, WindowMs - (now - windowTicks.Peek()));
                }
                else
                {
                    windowTicks.Enqueue(now);
                    windowSizes.Enqueue(bytes);
                    lastSendTicks = now;
                    return;
                }
            }
            Thread.Sleep((int)Math.Min(waitMs, 100));
        }
    }

    void EmitSmall(BrowserWindow target, string type, string message)
    {
        try
        {
            var json = new JObject { ["type"] = type, ["message"] = message }.ToString(Formatting.None);
            ContextTrace.Write($"-> {type} bytes={ContextStateBudget.EscapedBytes(json)}（替代被拒绝的超大报文）");
            Electron.IpcMain.Send(target, ChannelId, json);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "ContextManager 发送替代报文失败");
        }
    }

    void OnIpcMessage(object? payload)
    {
        if (payload == null) return;
        try
        {
            JsonElement root = ParsePayload(payload);
            if (root.ValueKind == JsonValueKind.Undefined || root.ValueKind == JsonValueKind.Null) return;
            if (root.TryGetProperty("type", out var typeElement) == false) return;
            string? type = typeElement.GetString();
            if (string.IsNullOrWhiteSpace(type)) return;
            OnMessage?.Invoke(type, root);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "ContextManager 解析渲染进程消息失败");
        }
    }

    static JsonElement ParsePayload(object payload)
    {
        if (payload is string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return default;
            return JsonDocument.Parse(json).RootElement.Clone();
        }

        if (payload is JsonElement element)
            return element.ValueKind == JsonValueKind.Object ? element : default;

        // ElectronNET 在不同配置下可能直接把对象交给主进程。转成 JSON 后统一解析。
        var serialized = JsonConvert.SerializeObject(payload);
        return JsonDocument.Parse(serialized).RootElement.Clone();
    }

    public void Dispose()
    {
        disposed = true;
        try { queueSignal.Release(); } catch { }
        var thread = sender;
        if (thread != null && thread.IsAlive)
        {
            // 出站线程是后台线程：这里只等一下让它自然收尾，绝不无限等待。
            try { thread.Join(800); } catch { }
        }
        watchdog?.Dispose();
        watchdog = null;
        Electron.IpcMain.RemoveAllListeners(ChannelId);
        SetWindow(null);
    }
}
