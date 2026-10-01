using System.Threading.Tasks;
using Alife.Framework;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Rendering;

namespace Marisa.ContextManager;

/// <summary>
/// 插件配置页。
///
/// ⚠️ 这里有个容易踩的坑：模块一旦提供了自定义 <c>editorUI</c>，框架就**不再渲染**
/// 自动生成的配置表单了（见 Alife.Client 的 ModuleDetailView：
/// <c>@if (EditorUI != null) 自定义 else DefaultUI</c>）。
/// 也就是说配置项不会「自动出现在页面上」，必须由本组件显式把 <see cref="DefaultUI"/>
/// 渲染出来。本组件以前没有渲染它，所以 <c>OverrideMode</c> / <c>ApplyMacros</c>
/// 这些真实生效的配置项在配置页是看不到的；本轮新增的两个权限开关同样依赖这一步。
/// </summary>
public class ContextManagerUI : ModuleUIBase<ContextManagerModule, ContextManagerConfig>
{
    bool opening;

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        base.BuildRenderTree(builder);

        builder.OpenElement(0, "div");
        builder.AddAttribute(1, "style", "padding:14px 4px;display:flex;flex-direction:column;gap:14px");

        builder.OpenElement(2, "div");
        builder.AddAttribute(3, "style", "font-size:13.5px;color:#3a4252;line-height:1.75");
        builder.AddContent(4, "按角色加载完整上下文，编辑消息身份与模块顺序，支持插件覆盖、本地覆盖和酒馆资源导入。");
        builder.CloseElement();

        builder.OpenElement(5, "button");
        builder.AddAttribute(6, "type", "button");
        builder.AddAttribute(7, "style", "align-self:flex-start;border:0;border-radius:8px;background:#7c3aed;color:#fff;padding:8px 18px;font-size:13px;cursor:pointer");
        builder.AddAttribute(8, "disabled", opening);
        builder.AddAttribute(9, "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, OpenWindowAsync));
        builder.AddContent(10, opening ? "正在打开…" : "打开上下文管理器");
        builder.CloseElement();

        // 说明下面两个「允许角色修改」开关的实际含义 —— 光看开关名不够，
        // 用户需要知道「开了之后角色能干什么、要经过什么」才敢开。
        builder.OpenElement(11, "div");
        builder.AddAttribute(12, "style", "font-size:12.5px;color:#69707d;line-height:1.75;padding:10px 12px;border:1px solid #e6e2f5;border-radius:9px;background:#faf8ff");
        builder.AddContent(13, "下面两个开关默认关闭。打开后，角色可以自己查看并申请修改上下文 —— "
            + "但每一次改动都会弹窗请你批准，改完还要再经你批准「应用」才会影响真实对话。"
            + "改动默认走「插件覆盖」，不修改角色文件。");
        builder.CloseElement();

        // ⚠️ 必须渲染 —— 这是框架生成的配置表单（OverrideMode / 酒馆预设 / 宏开关 / 两个新权限开关）。
        builder.AddContent(14, DefaultUI);

        builder.CloseElement();
    }

    async Task OpenWindowAsync()
    {
        if (Module == null || opening) return;
        opening = true;
        StateHasChanged();
        try { await Module.OpenWindowAsync(); }
        finally
        {
            opening = false;
            StateHasChanged();
        }
    }
}
