using PopTranslate.Core.Models;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace PopTranslate.Win32.Screenshot;

internal sealed class FrozenMonitorOverlayWindow : IDisposable
{
    private const uint WmEraseBkgnd = 0x0014;
    private const uint WmPaint = 0x000F;
    private const uint WmSetCursor = 0x0020;
    private const uint WmKeyDown = 0x0100;
    private const uint WmMouseMove = 0x0200;
    private const uint WmLButtonDown = 0x0201;
    private const uint WmLButtonUp = 0x0202;
    private const uint WmRButtonUp = 0x0205;
    private const uint WmCaptureChanged = 0x0215;

    private const int MinSelectionSidePx = 8;
    private static readonly COLORREF AccentBlueColorRef = new(0x00D47800u); // #0078D4 (BBGGRR)
    private static readonly COLORREF BadgeFillColorRef = new(0x00201C18u);  // Dark Fluent surface
    private static readonly COLORREF BadgeBorderColorRef = new(0x006A645Eu);
    private static readonly COLORREF WhiteColorRef = new(0x00FFFFFFu);
    private static readonly HWND HwndTopMost = new(-1);

    private readonly Action<CapturedMonitorBitmap, ScreenRect> _onCompleted;
    private readonly Action _onCancelled;
    private readonly Action? _onPainted;

    private HDC _backBufferDc = HDC.Null;
    private HBITMAP _backBufferBitmap = HBITMAP.Null;
    private HGDIOBJ _backBufferOldObject = HGDIOBJ.Null;
    private HBRUSH _accentBrush = HBRUSH.Null;
    private HBRUSH _badgeBrush = HBRUSH.Null;
    private HPEN _badgeBorderPen = HPEN.Null;
    private HFONT _badgeFont = HFONT.Null;

    private bool _isDragging;
    private ScreenPoint _dragStart;
    private ScreenPoint _dragCurrent;
    private bool _disposed;

    public FrozenMonitorOverlayWindow(
        CapturedMonitorBitmap monitorBitmap,
        Action<CapturedMonitorBitmap, ScreenRect> onCompleted,
        Action onCancelled,
        Action? onPainted = null)
    {
        MonitorBitmap = monitorBitmap ?? throw new ArgumentNullException(nameof(monitorBitmap));
        _onCompleted = onCompleted ?? throw new ArgumentNullException(nameof(onCompleted));
        _onCancelled = onCancelled ?? throw new ArgumentNullException(nameof(onCancelled));
        _onPainted = onPainted;
    }

    public CapturedMonitorBitmap MonitorBitmap { get; }

    public HWND Handle { get; private set; }

    public unsafe void Create(string className, HINSTANCE hInstance)
    {
        var bounds = MonitorBitmap.Monitor.Bounds;

        fixed (char* pClassName = className)
        {
            // Create hidden (without WS_VISIBLE) so caller can register HWND in the
            // dispatch table before Show() triggers the initial WM_ERASEBKGND / WM_PAINT.
            Handle = PInvoke.CreateWindowEx(
                WINDOW_EX_STYLE.WS_EX_TOPMOST | WINDOW_EX_STYLE.WS_EX_TOOLWINDOW,
                pClassName,
                null,
                WINDOW_STYLE.WS_POPUP,
                bounds.X,
                bounds.Y,
                bounds.Width,
                bounds.Height,
                HWND.Null,
                HMENU.Null,
                hInstance,
                null);
        }

        if (Handle == HWND.Null)
        {
            throw new InvalidOperationException($"Failed to create frozen overlay window for monitor '{MonitorBitmap.Monitor.DeviceName}'.");
        }

        InitializeGdiResources();
    }

    public unsafe void Show()
    {
        if (Handle == HWND.Null)
        {
            return;
        }

        var bounds = MonitorBitmap.Monitor.Bounds;
        PInvoke.SetWindowPos(
            Handle,
            HwndTopMost,
            bounds.X,
            bounds.Y,
            bounds.Width,
            bounds.Height,
            SET_WINDOW_POS_FLAGS.SWP_SHOWWINDOW);

        PInvoke.InvalidateRect(Handle, (RECT*)null, false);
        PInvoke.UpdateWindow(Handle);
    }

    public unsafe LRESULT HandleMessage(HWND hwnd, uint msg, WPARAM wParam, LPARAM lParam)
    {
        switch (msg)
        {
            case WmEraseBkgnd:
                return new LRESULT(1);

            case WmSetCursor:
            {
                var crossCursor = PInvoke.LoadCursor(HINSTANCE.Null, new PCWSTR((char*)32515)); // IDC_CROSS
                if (crossCursor != HCURSOR.Null)
                {
                    PInvoke.SetCursor(crossCursor);
                    return new LRESULT(1);
                }

                break;
            }

            case WmPaint:
                Paint(hwnd);
                return new LRESULT(0);

            case WmLButtonDown:
                _isDragging = true;
                _dragStart = ExtractClientPoint(lParam);
                _dragCurrent = _dragStart;
                PInvoke.SetCapture(hwnd);
                PInvoke.InvalidateRect(hwnd, (RECT*)null, false);
                return new LRESULT(0);

            case WmMouseMove:
                if (_isDragging)
                {
                    _dragCurrent = ExtractClientPoint(lParam);
                    PInvoke.InvalidateRect(hwnd, (RECT*)null, false);
                }

                return new LRESULT(0);

            case WmLButtonUp:
                if (_isDragging)
                {
                    _isDragging = false;
                    _dragCurrent = ExtractClientPoint(lParam);
                    PInvoke.ReleaseCapture();

                    var selection = ScreenCaptureGdi.NormalizeSelectionRect(
                        _dragStart,
                        _dragCurrent,
                        MonitorBitmap.Width,
                        MonitorBitmap.Height);

                    if (selection.Width >= MinSelectionSidePx && selection.Height >= MinSelectionSidePx)
                    {
                        _onCompleted(MonitorBitmap, selection);
                    }
                    else
                    {
                        PInvoke.InvalidateRect(hwnd, (RECT*)null, false);
                    }
                }

                return new LRESULT(0);

            case WmCaptureChanged:
                if (_isDragging)
                {
                    _isDragging = false;
                    PInvoke.InvalidateRect(hwnd, (RECT*)null, false);
                }

                return new LRESULT(0);

            case WmRButtonUp:
                if (_isDragging)
                {
                    _isDragging = false;
                    PInvoke.ReleaseCapture();
                }

                _onCancelled();
                return new LRESULT(0);

            case WmKeyDown:
                if ((VIRTUAL_KEY)wParam.Value == VIRTUAL_KEY.VK_ESCAPE)
                {
                    if (_isDragging)
                    {
                        _isDragging = false;
                        PInvoke.ReleaseCapture();
                    }

                    _onCancelled();
                    return new LRESULT(0);
                }

                break;
        }

        return PInvoke.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    public void DestroyWindowIfAlive()
    {
        if (Handle != HWND.Null && PInvoke.IsWindow(Handle))
        {
            PInvoke.DestroyWindow(Handle);
        }

        Handle = HWND.Null;
    }

    public unsafe void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DestroyWindowIfAlive();

        if (!_backBufferDc.IsNull)
        {
            if (!_backBufferOldObject.IsNull)
            {
                PInvoke.SelectObject(_backBufferDc, _backBufferOldObject);
                _backBufferOldObject = HGDIOBJ.Null;
            }

            PInvoke.DeleteDC(_backBufferDc);
            _backBufferDc = HDC.Null;
        }

        if (!_backBufferBitmap.IsNull)
        {
            PInvoke.DeleteObject(new HGDIOBJ(_backBufferBitmap.Value));
            _backBufferBitmap = HBITMAP.Null;
        }

        if (!_accentBrush.IsNull)
        {
            PInvoke.DeleteObject(new HGDIOBJ(_accentBrush.Value));
            _accentBrush = HBRUSH.Null;
        }

        if (!_badgeBrush.IsNull)
        {
            PInvoke.DeleteObject(new HGDIOBJ(_badgeBrush.Value));
            _badgeBrush = HBRUSH.Null;
        }

        if (!_badgeBorderPen.IsNull)
        {
            PInvoke.DeleteObject(new HGDIOBJ(_badgeBorderPen.Value));
            _badgeBorderPen = HPEN.Null;
        }

        if (!_badgeFont.IsNull)
        {
            PInvoke.DeleteObject(new HGDIOBJ(_badgeFont.Value));
            _badgeFont = HFONT.Null;
        }
    }

    private unsafe void InitializeGdiResources()
    {
        var windowDc = PInvoke.GetDC(Handle);
        try
        {
            _backBufferDc = PInvoke.CreateCompatibleDC(windowDc);
            _backBufferBitmap = PInvoke.CreateCompatibleBitmap(windowDc, MonitorBitmap.Width, MonitorBitmap.Height);
            if (!_backBufferDc.IsNull && !_backBufferBitmap.IsNull)
            {
                _backBufferOldObject = PInvoke.SelectObject(_backBufferDc, new HGDIOBJ(_backBufferBitmap.Value));
            }
        }
        finally
        {
            if (!windowDc.IsNull)
            {
                PInvoke.ReleaseDC(Handle, windowDc);
            }
        }

        _accentBrush = PInvoke.CreateSolidBrush(AccentBlueColorRef);
        _badgeBrush = PInvoke.CreateSolidBrush(BadgeFillColorRef);
        _badgeBorderPen = PInvoke.CreatePen(PEN_STYLE.PS_SOLID, 1, BadgeBorderColorRef);

        var dpiScale = MonitorBitmap.Monitor.DpiScale;
        var fontHeight = -(int)Math.Round(13.0 * dpiScale);
        var logFont = new LOGFONTW
        {
            lfHeight = fontHeight,
            lfWeight = 600, // FW_SEMIBOLD
            lfCharSet = FONT_CHARSET.DEFAULT_CHARSET,
            lfOutPrecision = FONT_OUTPUT_PRECISION.OUT_DEFAULT_PRECIS,
            lfClipPrecision = FONT_CLIP_PRECISION.CLIP_DEFAULT_PRECIS,
            lfQuality = FONT_QUALITY.CLEARTYPE_QUALITY,
            lfFaceName = "Segoe UI",
        };
        _badgeFont = PInvoke.CreateFontIndirect(&logFont);
    }

    private unsafe void Paint(HWND hwnd)
    {
        var hdc = PInvoke.BeginPaint(hwnd, out var paintStruct);
        try
        {
            if (hdc.IsNull)
            {
                return;
            }

            var targetDc = !_backBufferDc.IsNull ? _backBufferDc : hdc;

            // 1. Paint full dimmed frozen monitor image.
            PInvoke.BitBlt(
                targetDc,
                0,
                0,
                MonitorBitmap.Width,
                MonitorBitmap.Height,
                MonitorBitmap.DimmedDc,
                0,
                0,
                ROP_CODE.SRCCOPY);

            // 2. If dragging, reveal crisp undimmed pixels inside selection + accent border + dimension badge.
            if (_isDragging)
            {
                var selection = ScreenCaptureGdi.NormalizeSelectionRect(
                    _dragStart,
                    _dragCurrent,
                    MonitorBitmap.Width,
                    MonitorBitmap.Height);

                if (selection.Width > 0 && selection.Height > 0)
                {
                    PInvoke.BitBlt(
                        targetDc,
                        selection.X,
                        selection.Y,
                        selection.Width,
                        selection.Height,
                        MonitorBitmap.BrightDc,
                        selection.X,
                        selection.Y,
                        ROP_CODE.SRCCOPY);

                    DrawSelectionBorder(targetDc, selection);
                    DrawDimensionBadge(targetDc, selection);
                }
            }

            if (targetDc != hdc)
            {
                PInvoke.BitBlt(
                    hdc,
                    0,
                    0,
                    MonitorBitmap.Width,
                    MonitorBitmap.Height,
                    targetDc,
                    0,
                    0,
                    ROP_CODE.SRCCOPY);
            }

            _onPainted?.Invoke();
        }
        finally
        {
            PInvoke.EndPaint(hwnd, paintStruct);
        }
    }

    private unsafe void DrawSelectionBorder(HDC dc, ScreenRect selection)
    {
        if (_accentBrush.IsNull)
        {
            return;
        }

        var thickness = Math.Max(2, (int)Math.Round(2.0 * MonitorBitmap.Monitor.DpiScale));
        var topRect = new RECT
        {
            left = selection.X,
            top = selection.Y,
            right = selection.Right,
            bottom = Math.Min(selection.Bottom, selection.Y + thickness),
        };
        var bottomRect = new RECT
        {
            left = selection.X,
            top = Math.Max(selection.Y, selection.Bottom - thickness),
            right = selection.Right,
            bottom = selection.Bottom,
        };
        var leftRect = new RECT
        {
            left = selection.X,
            top = selection.Y,
            right = Math.Min(selection.Right, selection.X + thickness),
            bottom = selection.Bottom,
        };
        var rightRect = new RECT
        {
            left = Math.Max(selection.X, selection.Right - thickness),
            top = selection.Y,
            right = selection.Right,
            bottom = selection.Bottom,
        };

        PInvoke.FillRect(dc, &topRect, _accentBrush);
        PInvoke.FillRect(dc, &bottomRect, _accentBrush);
        PInvoke.FillRect(dc, &leftRect, _accentBrush);
        PInvoke.FillRect(dc, &rightRect, _accentBrush);
    }

    private unsafe void DrawDimensionBadge(HDC dc, ScreenRect selection)
    {
        if (_badgeBrush.IsNull || _badgeFont.IsNull)
        {
            return;
        }

        var scale = MonitorBitmap.Monitor.DpiScale;
        var label = $"{selection.Width} × {selection.Height}";
        var padX = (int)Math.Round(8 * scale);
        var padY = (int)Math.Round(4 * scale);
        var margin = (int)Math.Round(8 * scale);
        var cornerRadius = (int)Math.Round(6 * scale);

        var oldFont = PInvoke.SelectObject(dc, new HGDIOBJ(_badgeFont.Value));
        var oldBrush = PInvoke.SelectObject(dc, new HGDIOBJ(_badgeBrush.Value));
        var oldPen = !_badgeBorderPen.IsNull
            ? PInvoke.SelectObject(dc, new HGDIOBJ(_badgeBorderPen.Value))
            : HGDIOBJ.Null;
        var oldBkMode = PInvoke.SetBkMode(dc, BACKGROUND_MODE.TRANSPARENT);
        var oldTextColor = PInvoke.SetTextColor(dc, WhiteColorRef);

        try
        {
            var calcRect = new RECT();
            fixed (char* pLabel = label)
            {
                PInvoke.DrawText(
                    dc,
                    new PWSTR(pLabel),
                    label.Length,
                    ref calcRect,
                    DRAW_TEXT_FORMAT.DT_CALCRECT | DRAW_TEXT_FORMAT.DT_SINGLELINE);
            }

            var badgeW = (calcRect.right - calcRect.left) + padX * 2;
            var badgeH = (calcRect.bottom - calcRect.top) + padY * 2;

            var badgeX = Math.Clamp(selection.X, margin, Math.Max(margin, MonitorBitmap.Width - badgeW - margin));
            var badgeY = selection.Y - badgeH - margin >= 0
                ? selection.Y - badgeH - margin
                : Math.Min(selection.Y + margin, Math.Max(0, MonitorBitmap.Height - badgeH - margin));

            PInvoke.RoundRect(
                dc,
                badgeX,
                badgeY,
                badgeX + badgeW,
                badgeY + badgeH,
                cornerRadius,
                cornerRadius);

            var textRect = new RECT
            {
                left = badgeX,
                top = badgeY,
                right = badgeX + badgeW,
                bottom = badgeY + badgeH,
            };

            fixed (char* pLabel = label)
            {
                PInvoke.DrawText(
                    dc,
                    new PWSTR(pLabel),
                    label.Length,
                    ref textRect,
                    DRAW_TEXT_FORMAT.DT_CENTER | DRAW_TEXT_FORMAT.DT_VCENTER | DRAW_TEXT_FORMAT.DT_SINGLELINE);
            }
        }
        finally
        {
            PInvoke.SetTextColor(dc, oldTextColor);
            PInvoke.SetBkMode(dc, (BACKGROUND_MODE)oldBkMode);
            if (!oldPen.IsNull) PInvoke.SelectObject(dc, oldPen);
            if (!oldBrush.IsNull) PInvoke.SelectObject(dc, oldBrush);
            if (!oldFont.IsNull) PInvoke.SelectObject(dc, oldFont);
        }
    }

    private static ScreenPoint ExtractClientPoint(LPARAM lParam)
    {
        var packed = unchecked((int)lParam.Value);
        var x = unchecked((short)(packed & 0xFFFF));
        var y = unchecked((short)((packed >> 16) & 0xFFFF));
        return new ScreenPoint(x, y);
    }
}
