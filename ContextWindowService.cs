using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using ElectronNET.API;
using ElectronNET.API.Entities;
using Microsoft.Extensions.Logging;

namespace Marisa.ContextManager;

public sealed class ContextWindowService : IDisposable
{
    readonly ILogger logger;
    ContextIpcBridge? bridge;
    BrowserWindow? window;
    bool disposed;

    public event Action<string, JsonElement>? OnMessage;
    public event Action? RendererReady;

    public ContextWindowService(ILogger logger) => this.logger = logger;

    public async Task OpenAsync(string wwwRoot, string snapshotPath = "")
    {
        if (disposed) return;
        if (window == null)
        {
            await CreateAsync(wwwRoot, snapshotPath);
            ShowAndFocus();
            return;
        }
        ShowAndFocus();
    }

    async Task CreateAsync(string wwwRoot, string snapshotPath)
    {
        bridge = new ContextIpcBridge(logger);
        bridge.OnMessage += OnRendererMessage;
        // IpcMain.On is asynchronous. Do not let the renderer send its initial
        // refresh (or a fast user click) before the listener exists.
        await bridge.RegisterAsync();
        var snapshotQuery = string.IsNullOrWhiteSpace(snapshotPath)
            ? ""
            : "&snapshot=" + Uri.EscapeDataString(snapshotPath);
        string url = new Uri(Path.Combine(wwwRoot, "index.html")).AbsoluteUri
                     + $"?channel={bridge.ChannelId}&v={DateTime.UtcNow.Ticks}{snapshotQuery}";

        BrowserWindowOptions options = new()
        {
            Title = "上下文控制台",
            Width = 1280,
            Height = 820,
            MinWidth = 1040,
            MinHeight = 680,
            Show = false,
            Frame = false,
            RoundedCorners = true,
            Transparent = false,
            Resizable = true,
            Movable = true,
            SkipTaskbar = false,
            AlwaysOnTop = false,
            AutoHideMenuBar = true,
            Fullscreenable = true,
            WebPreferences = new WebPreferences
            {
                // 使用独立持久分区，避免主应用 Blazor Circuit 的会话恢复串到插件窗口
                Partition = "persist:marisa-context-manager",
                NodeIntegration = true,
                ContextIsolation = false,
                Sandbox = false,
                WebSecurity = true,
                DevTools = true
            }
        };

        window = await Electron.WindowManager.CreateWindowAsync(options, url);
        bridge.SetWindow(window);
        window.OnClosed += OnWindowClosed;
        window.WebContents.OnDidFinishLoad += () => RendererReady?.Invoke();
    }

    public BrowserWindow? Window => window;

    public void Minimize() => window?.Minimize();
    bool maximized;

    public void ToggleMaximize()
    {
        if (window == null) return;
        maximized = !maximized;
        if (maximized) window.Maximize();
        else window.Unmaximize();
    }

    public void Close() => window?.Close();

    void OnRendererMessage(string type, JsonElement payload) => OnMessage?.Invoke(type, payload);
    public void Send(string type, object? payload = null) => bridge?.Send(type, payload);
    public void ShowAndFocus() { window?.Show(); window?.Focus(); }

    void OnWindowClosed()
    {
        if (bridge != null)
        {
            bridge.OnMessage -= OnRendererMessage;
            bridge.Dispose();
        }
        bridge = null;
        window = null;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (bridge != null)
        {
            bridge.OnMessage -= OnRendererMessage;
            bridge.Dispose();
        }
        bridge = null;
        if (window != null)
        {
            try { window.Close(); }
            catch (Exception ex) { logger.LogWarning(ex, "关闭 ContextManager 窗口失败"); }
        }
        window = null;
    }
}






