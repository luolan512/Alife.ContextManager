using System.Threading.Tasks;
using Alife.Framework;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Rendering;

namespace Marisa.ContextManager;

public class ContextManagerUI : ModuleUIBase<ContextManagerModule, ContextManagerConfig>
{
    bool opening;

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        base.BuildRenderTree(builder);

        builder.OpenElement(0, "div");
        builder.AddAttribute(1, "style", "padding:14px 4px;display:flex;flex-direction:column;gap:12px");

        builder.OpenElement(2, "div");
        builder.AddAttribute(3, "style", "font-size:14px;color:#3a4252;line-height:1.7");
        builder.AddContent(4, "按角色加载完整上下文，编辑消息身份与模块顺序，支持临时注入（插件覆盖）、本地覆盖和酒馆资源导入。");
		builder.AddContent(4, "总之=v=先凑合用吧。");
        builder.CloseElement();

        builder.OpenElement(5, "button");
        builder.AddAttribute(6, "type", "button");
        builder.AddAttribute(7, "style", "align-self:flex-start;border:0;border-radius:8px;background:#7c3aed;color:#fff;padding:8px 18px;font-size:13px;cursor:pointer");
        builder.AddAttribute(8, "disabled", opening);
        builder.AddAttribute(9, "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, OpenWindowAsync));
        builder.AddContent(10, opening ? "正在打开…" : "打开上下文管理器");
        builder.CloseElement();

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
