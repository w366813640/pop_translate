namespace PopTranslate.Core.Models;

public sealed class SelectionConfig
{
    public static readonly IReadOnlyList<string> DefaultProcessBlacklist =
    [
        "WindowsTerminal.exe",
        "cmd.exe",
        "powershell.exe",
        "pwsh.exe",
        "conhost.exe",
        "mintty.exe",
        "wezterm-gui.exe",
        "alacritty.exe",
        "kitty.exe",
        "mstsc.exe",
        "vmconnect.exe",
        "vmware.exe",
        "VirtualBoxVM.exe",
        "League of Legends.exe",
        "VALORANT-Win64-Shipping.exe",
        "cs2.exe",
        "dota2.exe",
        "GenshinImpact.exe",
        "StarRail.exe",
        "Overwatch.exe",
        "eldenring.exe",
    ];

    public bool EnableHoverIcon { get; set; } = true;

    public bool EnableClipboardFallback { get; set; } = true;

    public bool QuietModeInFullscreen { get; set; } = true;

    public int HoverIconAutoDismissMs { get; set; } = 4000;

    public int MinDragDistancePixels { get; set; } = 12;

    public int ClipboardWaitTimeoutMs { get; set; } = 250;

    public List<string> ProcessBlacklist { get; set; } = [.. DefaultProcessBlacklist];

    public SelectionConfig Clone() => new()
    {
        EnableHoverIcon = EnableHoverIcon,
        EnableClipboardFallback = EnableClipboardFallback,
        QuietModeInFullscreen = QuietModeInFullscreen,
        HoverIconAutoDismissMs = HoverIconAutoDismissMs,
        MinDragDistancePixels = MinDragDistancePixels,
        ClipboardWaitTimeoutMs = ClipboardWaitTimeoutMs,
        ProcessBlacklist = [.. ProcessBlacklist],
    };
}
