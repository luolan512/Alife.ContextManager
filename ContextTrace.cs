using System;
using System.IO;

namespace Marisa.ContextManager;

/// <summary>
/// 极简文件追踪。用来证明“渲染进程发出的 IPC 是否进入后端、后端是否回发”，
/// 而不是把“编译成功”误当成交互验证成功。
/// 日志写在 Storage/ContextManager 下，避免放在会被重新编译/覆盖部署的插件目录里。
/// </summary>
public static class ContextTrace
{
    const int MaxBytes = 600_000;
    static readonly object Gate = new();
    static string? directoryPath;
    static bool disabled;

    /// <summary>
    /// 兜底日志目录。**`Configure` 之前写的日志以前会被静默丢弃**（见下），
    /// 而插件最早、最关键的几步（`OnAwake` 里注册函数、注入提示词）恰好都发生在
    /// `Configure` 之前 —— `Configure` 只在**打开插件窗口**时才调用。
    /// 结果是：那时段出的问题在 trace.log 里**一行都看不到**，
    /// 「插件静默变成不存在」这类故障因此完全无从排查（框架还会把 OnAwake 的异常吃掉，
    /// 只写 AlifeLog，我们同样看不到）。
    /// 现在先落到插件目录下的 `startup-trace.log`，`Configure` 之后再切到正规位置。
    /// </summary>
    static string? fallbackDirectory;

    /// <summary>设置兜底目录（插件自己的目录，一定可写）。启动时调用。</summary>
    public static void ConfigureFallback(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return;
        try
        {
            Directory.CreateDirectory(directory);
            fallbackDirectory = directory;
        }
        catch { fallbackDirectory = null; }
    }

    /// <summary>由运行时在拿到 Storage 根目录后调用一次。</summary>
    public static void Configure(string? storageRoot)
    {
        if (string.IsNullOrWhiteSpace(storageRoot)) return;
        try
        {
            var dir = Path.Combine(storageRoot, "ContextManager");
            Directory.CreateDirectory(dir);
            directoryPath = dir;
            disabled = false;
        }
        catch
        {
            disabled = true;
        }
    }

    public static string? DirectoryPath => directoryPath;

    public static void Write(string line)
    {
        if (disabled) return;
        // 正规目录优先；还没有就退到兜底目录 —— **绝不静默丢弃**。
        var dir = directoryPath ?? fallbackDirectory;
        if (dir == null) return;
        try
        {
            lock (Gate)
            {
                var file = Path.Combine(dir, directoryPath != null ? "trace.log" : "startup-trace.log");
                if (File.Exists(file) && new FileInfo(file).Length > MaxBytes)
                {
                    var existing = File.ReadAllText(file);
                    File.WriteAllText(file, existing[^Math.Min(existing.Length, MaxBytes / 2)..]);
                }
                File.AppendAllText(file, $"[{DateTime.Now:HH:mm:ss.fff}] {line}\n");
            }
        }
        catch
        {
            disabled = true;
        }
    }
}
