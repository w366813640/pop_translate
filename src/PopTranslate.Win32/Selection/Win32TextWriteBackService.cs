using System.Runtime.InteropServices;
using PopTranslate.Core.Contracts;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace PopTranslate.Win32.Selection;

public sealed class Win32TextWriteBackService : ITextWriteBackService
{
    private const int FocusSettleDelayMs = 35;
    private const int PasteConsumptionDelayMs = 80;

    private readonly IWindowHelperService _windowHelper;
    private readonly ClipboardBackupService _clipboardBackup;
    private readonly SemaphoreSlim _writeBackGate = new(1, 1);

    public Win32TextWriteBackService(
        IWindowHelperService windowHelper,
        ClipboardBackupService clipboardBackup)
    {
        _windowHelper = windowHelper ?? throw new ArgumentNullException(nameof(windowHelper));
        _clipboardBackup = clipboardBackup ?? throw new ArgumentNullException(nameof(clipboardBackup));
    }

    public async Task<bool> ReplaceTextInWindowAsync(
        nint targetWindowHandle,
        string replacementText,
        CancellationToken cancellationToken = default)
    {
        if (targetWindowHandle == nint.Zero || string.IsNullOrEmpty(replacementText))
        {
            return false;
        }

        var targetHwnd = new HWND(targetWindowHandle);
        if (!PInvoke.IsWindow(targetHwnd))
        {
            return false;
        }

        await _writeBackGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!PInvoke.IsWindow(targetHwnd))
            {
                return false;
            }

            var snapshot = await _clipboardBackup.CaptureSnapshotAsync(cancellationToken).ConfigureAwait(false);
            var clipboardModified = false;

            try
            {
                _windowHelper.ForceForegroundWindow(targetWindowHandle);
                await Task.Delay(FocusSettleDelayMs, cancellationToken).ConfigureAwait(false);

                if (!PInvoke.IsWindow(targetHwnd) || !IsTargetForegroundActive(targetHwnd))
                {
                    return false;
                }

                if (!await _clipboardBackup
                        .TryWriteUnicodeTextAsync(replacementText, includeAntiPollutionMarkers: true, cancellationToken)
                        .ConfigureAwait(false))
                {
                    return false;
                }

                clipboardModified = true;

                await ClipboardFallbackSelectionReader.WaitForModifiersReleaseAsync(cancellationToken)
                    .ConfigureAwait(false);

                if (!SendCtrlV())
                {
                    return false;
                }

                await Task.Delay(PasteConsumptionDelayMs, cancellationToken).ConfigureAwait(false);
                return true;
            }
            finally
            {
                if (clipboardModified && snapshot.CaptureSucceeded)
                {
                    await _clipboardBackup
                        .RestoreSnapshotAsync(snapshot, includeAntiPollutionMarkers: true, CancellationToken.None)
                        .ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _writeBackGate.Release();
        }
    }

    private static bool IsTargetForegroundActive(HWND targetHwnd)
    {
        var foregroundHwnd = PInvoke.GetForegroundWindow();
        if (foregroundHwnd == HWND.Null)
        {
            return false;
        }

        if (foregroundHwnd == targetHwnd)
        {
            return true;
        }

        var targetRoot = PInvoke.GetAncestor(targetHwnd, GET_ANCESTOR_FLAGS.GA_ROOT);
        if (targetRoot == HWND.Null)
        {
            targetRoot = targetHwnd;
        }

        var fgRoot = PInvoke.GetAncestor(foregroundHwnd, GET_ANCESTOR_FLAGS.GA_ROOT);
        if (fgRoot == HWND.Null)
        {
            fgRoot = foregroundHwnd;
        }

        return fgRoot == targetRoot;
    }

    private static unsafe bool SendCtrlV()
    {
        var userHoldingCtrl = ClipboardFallbackSelectionReader.IsControlPhysicallyPressed();
        var inputs = new List<INPUT>(12);

        // Press VK_CONTROL first to prevent SC_KEYMENU if Alt was held, then release Shift/Alt/Win
        // (e.g. when triggered from Ctrl+Shift+Enter) so target app receives clean Ctrl+V.
        inputs.Add(ClipboardFallbackSelectionReader.CreateKeyInput(VIRTUAL_KEY.VK_CONTROL, isKeyUp: false));

        ClipboardFallbackSelectionReader.ReleaseIfPressed(inputs, VIRTUAL_KEY.VK_LMENU);
        ClipboardFallbackSelectionReader.ReleaseIfPressed(inputs, VIRTUAL_KEY.VK_RMENU);
        ClipboardFallbackSelectionReader.ReleaseIfPressed(inputs, VIRTUAL_KEY.VK_MENU);
        ClipboardFallbackSelectionReader.ReleaseIfPressed(inputs, VIRTUAL_KEY.VK_LSHIFT);
        ClipboardFallbackSelectionReader.ReleaseIfPressed(inputs, VIRTUAL_KEY.VK_RSHIFT);
        ClipboardFallbackSelectionReader.ReleaseIfPressed(inputs, VIRTUAL_KEY.VK_SHIFT);
        ClipboardFallbackSelectionReader.ReleaseIfPressed(inputs, VIRTUAL_KEY.VK_LWIN);
        ClipboardFallbackSelectionReader.ReleaseIfPressed(inputs, VIRTUAL_KEY.VK_RWIN);

        inputs.Add(ClipboardFallbackSelectionReader.CreateKeyInput(VIRTUAL_KEY.VK_V, isKeyUp: false));
        inputs.Add(ClipboardFallbackSelectionReader.CreateKeyInput(VIRTUAL_KEY.VK_V, isKeyUp: true));

        if (!userHoldingCtrl)
        {
            inputs.Add(ClipboardFallbackSelectionReader.CreateKeyInput(VIRTUAL_KEY.VK_CONTROL, isKeyUp: true));
        }

        var span = CollectionsMarshal.AsSpan(inputs);
        return PInvoke.SendInput(span, sizeof(INPUT)) > 0;
    }
}
