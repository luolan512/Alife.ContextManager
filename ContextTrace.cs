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
        if (disabled || directoryPath == null) return;
        try
        {
            lock (Gate)
            {
                var file = Path.Combine(directoryPath, "trace.log");
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
