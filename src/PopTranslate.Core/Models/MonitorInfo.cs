namespace PopTranslate.Core.Models;

public sealed record MonitorInfo(
    nint Handle,
    string DeviceName,
    ScreenRect Bounds,
    ScreenRect WorkArea,
    uint DpiX,
    uint DpiY,
    bool IsPrimary)
{
    public double DpiScale => DpiX == 0 ? 1.0 : DpiX / 96.0;
}
