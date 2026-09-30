using System;
using System.Threading.Tasks;
using Alife.Framework;
using Microsoft.Extensions.Logging;

namespace Marisa.ContextManager;

[Module(
    "上下文管理器",
    "高效查看角色预设、系统提示词、记忆、对话历史、便签、技能与多模态上下文。",
    defaultCategory: "Marisa",
    editorUI: typeof(ContextManagerUI))]
public class ContextManagerModule(
    CharacterSystem characterSystem,
    ChatActivitySystem chatActivitySystem,
    StorageSystem storageSystem,
    PluginSystem pluginSystem,
    ModuleSystem moduleSystem,
    ILogger<ContextManagerModule> logger) :
    ChatBehaviour,
    IConfigurable<ContextManagerConfig>
{
    public ContextManagerConfig Configuration { get; set; } = new();
    public ContextManagerRuntime? Runtime { get; private set; }

    protected override Task OnAwake()
    {
        Runtime = ContextManagerRuntime.GetOrCreate(characterSystem, chatActivitySystem, storageSystem, pluginSystem, moduleSystem, logger);
        Runtime.ApplyConfig(Configuration);
        return Task.CompletedTask;
    }

    protected override Task OnUpdate()
    {
        Runtime?.ApplyConfig(Configuration);
        return Task.CompletedTask;
    }

    public Task OpenWindowAsync() => Runtime?.OpenWindowAsync() ?? Task.CompletedTask;

    protected override Task OnDestroy()
    {
        Runtime?.Release();
        Runtime = null;
        return Task.CompletedTask;
    }
}

