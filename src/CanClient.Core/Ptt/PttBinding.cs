using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace CanClient.Core.Ptt;

public enum PttKind
{
    Keyboard,
    Mouse,
    Joystick
}

/// <summary>
/// 一条 PTT 绑定，任意一条按住即发话。
///
/// 配置文件里的形状沿用 Python 版（键名改了就等于让所有老配置里的绑定静默失效）：
///     {"kind": "keyboard", "key": "`"}              Python 版存的是 pynput 的键名
///     {"kind": "keyboard", "key": "ctrl_r", "vk": 163}   C# 版多存一个虚拟键码
///     {"kind": "mouse", "button": "x1"}
///     {"kind": "joystick", "button": 2, "device": 0, "device_name": "T.16000M"}
///
/// 鼠标只认侧键 X1 / X2：监听是全局的，把左键绑成 PTT，用户在任何窗口点任何东西都会发话。
/// </summary>
[JsonConverter(typeof(PttBindingConverter))]
public sealed record PttBinding(PttKind Kind, int VirtualKey = 0, string Key = "", string MouseButton = "",
    int Button = 0, int Device = 0, string DeviceName = "")
{
    public static PttBinding Keyboard(int vk) => new(PttKind.Keyboard, vk, KeyNames.NameOf(vk));
    public static PttBinding Mouse(string button) => new(PttKind.Mouse, MouseButton: button);
    public static PttBinding Joystick(int device, int button, string name) =>
        new(PttKind.Joystick, Button: button, Device: device, DeviceName: name);

    /// <summary>界面上显示的说法，如"键盘 右 Ctrl"、"T.16000M 的按钮 3"。</summary>
    public string Describe() => Kind switch
    {
        PttKind.Keyboard => $"键盘 {KeyNames.Display(VirtualKey)}",
        PttKind.Mouse => MouseButton == "x2" ? "鼠标侧键 2" : "鼠标侧键 1",
        _ => $"{(DeviceName.Length > 0 ? DeviceName : $"摇杆 {Device + 1}")} 的按钮 {Button + 1}"
    };

    public bool Matches(PttBinding other) => Kind switch
    {
        PttKind.Keyboard => other.Kind == PttKind.Keyboard && other.VirtualKey == VirtualKey,
        PttKind.Mouse => other.Kind == PttKind.Mouse && other.MouseButton == MouseButton,
        _ => other.Kind == PttKind.Joystick && other.Device == Device && other.Button == Button
    };
    public string Label => Describe();
}

internal sealed class PttBindingConverter : JsonConverter<PttBinding>
{
    public override PttBinding? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var node = JsonNode.Parse(ref reader) as JsonObject;
        return node is null ? null : FromJson(node);
    }

    public static PttBinding? FromJson(JsonObject node)
    {
        var kind = node["kind"]?.GetValue<string>();
        try
        {
            switch (kind)
            {
                case "keyboard":
                {
                    var key = (node["key"]?.ToString() ?? "").ToLowerInvariant();
                    var vk = node["vk"] is JsonValue v && v.TryGetValue(out int code) ? code : KeyNames.FromPynput(key);
                    return vk > 0 ? new PttBinding(PttKind.Keyboard, vk, key.Length > 0 ? key : KeyNames.NameOf(vk)) : null;
                }
                case "mouse":
                {
                    var button = (node["button"]?.ToString() ?? "").ToLowerInvariant();
                    return button is "x1" or "x2" ? PttBinding.Mouse(button) : null;
                }
                case "joystick":
                {
                    var button = int.Parse(node["button"]!.ToString());
                    if (button < 0)
                        return null;
                    var device = node["device"] is { } d ? int.Parse(d.ToString()) : 0;
                    return PttBinding.Joystick(device, button, node["device_name"]?.ToString() ?? "");
                }
            }
        }
        catch (Exception e) when (e is FormatException or InvalidOperationException or NullReferenceException)
        {
            return null;
        }
        return null;
    }

    public override void Write(Utf8JsonWriter writer, PttBinding value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        switch (value.Kind)
        {
            case PttKind.Keyboard:
                writer.WriteString("kind", "keyboard");
                writer.WriteString("key", value.Key);
                writer.WriteNumber("vk", value.VirtualKey);
                break;
            case PttKind.Mouse:
                writer.WriteString("kind", "mouse");
                writer.WriteString("button", value.MouseButton);
                break;
            default:
                writer.WriteString("kind", "joystick");
                writer.WriteNumber("button", value.Button);
                writer.WriteNumber("device", value.Device);
                writer.WriteString("device_name", value.DeviceName);
                break;
        }
        writer.WriteEndObject();
    }
}

/// <summary>虚拟键码和键名之间的换算，包括 Python 版 pynput 键名的迁移。</summary>
public static class KeyNames
{
    private static readonly Dictionary<string, int> Pynput = new(StringComparer.OrdinalIgnoreCase)
    {
        ["space"] = 0x20, ["enter"] = 0x0D, ["tab"] = 0x09, ["backspace"] = 0x08, ["esc"] = 0x1B,
        ["shift"] = 0xA0, ["shift_l"] = 0xA0, ["shift_r"] = 0xA1,
        ["ctrl"] = 0xA2, ["ctrl_l"] = 0xA2, ["ctrl_r"] = 0xA3,
        ["alt"] = 0xA4, ["alt_l"] = 0xA4, ["alt_r"] = 0xA5, ["alt_gr"] = 0xA5,
        ["cmd"] = 0x5B, ["cmd_l"] = 0x5B, ["cmd_r"] = 0x5C, ["menu"] = 0x5D,
        ["caps_lock"] = 0x14, ["num_lock"] = 0x90, ["scroll_lock"] = 0x91, ["pause"] = 0x13,
        ["print_screen"] = 0x2C, ["insert"] = 0x2D, ["delete"] = 0x2E, ["home"] = 0x24, ["end"] = 0x23,
        ["page_up"] = 0x21, ["page_down"] = 0x22, ["up"] = 0x26, ["down"] = 0x28, ["left"] = 0x25, ["right"] = 0x27,
        ["`"] = 0xC0, ["-"] = 0xBD, ["="] = 0xBB, ["["] = 0xDB, ["]"] = 0xDD, ["\\"] = 0xDC,
        [";"] = 0xBA, ["'"] = 0xDE, [","] = 0xBC, ["."] = 0xBE, ["/"] = 0xBF
    };

    private static readonly Dictionary<int, string> DisplayNames = new()
    {
        [0x20] = "空格", [0x0D] = "回车", [0x09] = "Tab", [0x08] = "退格", [0x1B] = "Esc",
        [0xA0] = "左 Shift", [0xA1] = "右 Shift", [0x10] = "Shift",
        [0xA2] = "左 Ctrl", [0xA3] = "右 Ctrl", [0x11] = "Ctrl",
        [0xA4] = "左 Alt", [0xA5] = "右 Alt", [0x12] = "Alt",
        [0x5B] = "左 Win", [0x5C] = "右 Win", [0x5D] = "菜单键", [0x14] = "Caps Lock",
        [0x90] = "Num Lock", [0x91] = "Scroll Lock", [0x13] = "Pause", [0x2C] = "Print Screen",
        [0x2D] = "Insert", [0x2E] = "Delete", [0x24] = "Home", [0x23] = "End", [0x21] = "Page Up",
        [0x22] = "Page Down", [0x26] = "↑", [0x28] = "↓", [0x25] = "←", [0x27] = "→",
        [0xC0] = "`", [0xBD] = "-", [0xBB] = "=", [0xDB] = "[", [0xDD] = "]", [0xDC] = "\\",
        [0xBA] = ";", [0xDE] = "'", [0xBC] = ",", [0xBE] = ".", [0xBF] = "/",
        [0x6A] = "小键盘 *", [0x6B] = "小键盘 +", [0x6D] = "小键盘 -", [0x6E] = "小键盘 .", [0x6F] = "小键盘 /"
    };

    /// <summary>pynput 的键名（"`"、"ctrl_r"、"f5"、"a"、"vk96"）→ 虚拟键码。认不出返回 0。</summary>
    public static int FromPynput(string name)
    {
        if (string.IsNullOrEmpty(name))
            return 0;
        if (Pynput.TryGetValue(name, out var vk))
            return vk;
        if (name.StartsWith("vk", StringComparison.OrdinalIgnoreCase) && int.TryParse(name[2..], out var code))
            return code;
        if (name.Length >= 2 && name[0] is 'f' or 'F' && int.TryParse(name[1..], out var f) && f is >= 1 and <= 24)
            return 0x70 + f - 1;
        if (name.Length == 1)
        {
            var c = char.ToUpperInvariant(name[0]);
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9')
                return c;
        }
        return 0;
    }

    /// <summary>存进配置文件的键名。</summary>
    public static string NameOf(int vk)
    {
        foreach (var (name, code) in Pynput)
        {
            if (code == vk)
                return name;
        }
        if (vk is >= 0x70 and <= 0x87)
            return $"f{vk - 0x70 + 1}";
        if (vk is >= 'A' and <= 'Z' or >= '0' and <= '9')
            return ((char)vk).ToString().ToLowerInvariant();
        return $"vk{vk}";
    }

    /// <summary>界面上显示的键名。</summary>
    public static string DisplayName(int vk)
    {
        if (DisplayNames.TryGetValue(vk, out var name))
            return name;
        if (vk is >= 0x70 and <= 0x87)
            return $"F{vk - 0x70 + 1}";
        if (vk is >= 0x60 and <= 0x69)
            return $"小键盘 {vk - 0x60}";
        if (vk is >= 'A' and <= 'Z' or >= '0' and <= '9')
            return ((char)vk).ToString();
        return $"键码 {vk}";
    }

    public static string Display(int vk) => DisplayName(vk);
}
