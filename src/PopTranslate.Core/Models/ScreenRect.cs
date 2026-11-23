namespace PopTranslate.Core.Models;

/// <summary>
/// Screen rectangle in physical pixels.
/// </summary>
public readonly record struct ScreenRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;

    public int Bottom => Y + Height;

    public bool IsEmpty => Width <= 0 || Height <= 0;

    public bool Contains(ScreenPoint point) =>
        point.X >= X && point.X < Right &&
        point.Y >= Y && point.Y < Bottom;
}
