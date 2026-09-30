using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace Marisa.ContextManager;

/// <summary>
/// 两套序列化设置，唯一区别是缩进。抽出来是为了能单元测试，也避免各处各写一份。
///
///   Disk —— 落盘（Plans / CharacterPresets / 导出的 .contextpreset.json），缩进，
///           用户可以直接打开文件看内容。
///   Wire —— 过 IPC。state 报文里的 planJsonByOwner 是「字符串里的 JSON」，
///           会被外层再转义一次；缩进版会白白把报文撑大 30% 以上，
///           而报文过大正是这套 Electron.NET 桥最可能卡死的诱因。
/// </summary>
public static class ContextJsonSettings
{
    public static readonly JsonSerializerSettings Disk = new()
    {
        ContractResolver = new CamelCasePropertyNamesContractResolver(),
        Formatting = Formatting.Indented
    };

    public static readonly JsonSerializerSettings Wire = new()
    {
        ContractResolver = new CamelCasePropertyNamesContractResolver(),
        Formatting = Formatting.None
    };
}
