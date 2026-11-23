using System.Text.Json.Serialization;

namespace PopTranslate.Core.Models;

/// <summary>
/// Modifier bitflags aligned with Win32 MOD_ALT (0x1), MOD_CONTROL (0x2), MOD_SHIFT (0x4), MOD_WIN (0x8).
/// </summary>
[Flags]
public enum HotkeyModifiers : uint
{
    None = 0x0000,
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Win = 0x0008,
}

public sealed record HotkeyGesture(
    HotkeyModifiers Modifiers,
    uint VirtualKey)
{
    public static readonly HotkeyGesture Empty = new(HotkeyModifiers.None, 0);

    [JsonIgnore]
    public bool IsEmpty => VirtualKey == 0;

    [JsonIgnore]
    public string DisplayText => FormatDisplayText(Modifiers, VirtualKey);

    public static string FormatDisplayText(HotkeyModifiers modifiers, uint virtualKey)
    {
        if (virtualKey == 0)
        {
            return "None";
        }

        var parts = new List<string>(5);
        if (modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");
        parts.Add(FormatVirtualKey(virtualKey));
        return string.Join(" + ", parts);
    }

    public static string FormatVirtualKey(uint vk) => vk switch
    {
        >= 0x30 and <= 0x39 => ((char)vk).ToString(),
        >= 0x41 and <= 0x5A => ((char)vk).ToString(),
        >= 0x70 and <= 0x87 => $"F{vk - 0x6F}",
        0x20 => "Space",
        0x0D => "Enter",
        0x09 => "Tab",
        0x1B => "Esc",
        0x2E => "Delete",
        0x2D => "Insert",
        0x24 => "Home",
        0x23 => "End",
        0x21 => "PageUp",
        0x22 => "PageDown",
        0x25 => "Left",
        0x26 => "Up",
        0x27 => "Right",
        0x28 => "Down",
        0xBA => ";",
        0xBB => "=",
        0xBC => ",",
        0xBD => "-",
        0xBE => ".",
        0xBF => "/",
        0xC0 => "`",
        0xDB => "[",
        0xDC => "\\",
        0xDD => "]",
        0xDE => "'",
        _ => $"VK_0x{vk:X2}",
    };
}

public sealed class HotkeyConfig
{
    public static readonly HotkeyGesture DefaultSelectionTranslate = new(HotkeyModifiers.Alt, 0x44); // Alt + D
    public static readonly HotkeyGesture DefaultInputTranslate = new(HotkeyModifiers.Alt, 0x41); // Alt + A
    public static readonly HotkeyGesture DefaultScreenshotTranslate = new(HotkeyModifiers.Alt, 0x53); // Alt + S
    public static readonly HotkeyGesture DefaultSelectionPolish = new(HotkeyModifiers.Alt, 0x50); // Alt + P

    public HotkeyGesture SelectionTranslate { get; set; } = DefaultSelectionTranslate;

    public HotkeyGesture InputTranslate { get; set; } = DefaultInputTranslate;

    public HotkeyGesture ScreenshotTranslate { get; set; } = DefaultScreenshotTranslate;

    public HotkeyGesture SelectionPolish { get; set; } = DefaultSelectionPolish;

    public HotkeyConfig Clone() => new()
    {
        SelectionTranslate = SelectionTranslate ?? DefaultSelectionTranslate,
        InputTranslate = InputTranslate ?? DefaultInputTranslate,
        ScreenshotTranslate = ScreenshotTranslate ?? DefaultScreenshotTranslate,
        SelectionPolish = SelectionPolish ?? DefaultSelectionPolish,
    };
}

public sealed record HotkeyRegistrationResult(
    HotkeyAction Action,
    bool Succeeded,
    string? ErrorMessage = null);
