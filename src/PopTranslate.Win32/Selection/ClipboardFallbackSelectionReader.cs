using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace PopTranslate.Win32.Selection;

public class ClipboardFallbackSelectionReader
{
    private const int ModifierReleaseMaxWaitMs = 200;
    private const int PollIntervalMs = 8;

    private readonly ClipboardBackupService _backupService;
    private readonly SemaphoreSlim _captureGate = new(1, 1);

    public ClipboardFallbackSelectionReader(ClipboardBackupService backupService)
    {
        _backupService = backupService ?? throw new ArgumentNullException(nameof(backupService));
    }

    public virtual async Task<string?> TryCaptureViaCtrlCAsync(
        int waitTimeoutMs,
        CancellationToken cancellationToken = default)
    {
        await _captureGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var timeoutMs = Math.Clamp(waitTimeoutMs, 50, 2000);
            var snapshot = await _backupService.CaptureSnapshotAsync(cancellationToken).ConfigureAwait(false);

            // CRITICAL: Abort Ctrl+C fallback if backup failed (clipboard contention or un-backupable
            // CF_BITMAP/OLE content) so we never wipe out the user's prior clipboard!
            if (!snapshot.CaptureSucceeded)
            {
                return null;
            }

            var beforeSequence = PInvoke.GetClipboardSequenceNumber();
            var snapshotRestored = false;

            try
            {
                await WaitForModifiersReleaseAsync(cancellationToken).ConfigureAwait(false);

                if (!SendCtrlC())
                {
                    return null;
                }

                var sw = Stopwatch.StartNew();
                bool sequenceChanged = false;
                while (sw.ElapsedMilliseconds < timeoutMs)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (PInvoke.GetClipboardSequenceNumber() != beforeSequence)
                    {
                        sequenceChanged = true;
                        break;
                    }

                    await Task.Delay(PollIntervalMs, cancellationToken).ConfigureAwait(false);
                }

                if (!sequenceChanged)
                {
                    return null;
                }

                var (capturedText, restored) = await _backupService
                    .TryReadUnicodeTextAndRestoreAsync(snapshot, includeAntiPollutionMarkers: true, cancellationToken)
                    .ConfigureAwait(false);

                snapshotRestored = restored;
                return string.IsNullOrWhiteSpace(capturedText) ? null : capturedText;
            }
            finally
            {
                if (!snapshotRestored &&
                    snapshot.CaptureSucceeded &&
                    PInvoke.GetClipboardSequenceNumber() != beforeSequence)
                {
                    await _backupService
                        .RestoreSnapshotAsync(snapshot, includeAntiPollutionMarkers: true, CancellationToken.None)
                        .ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _captureGate.Release();
        }
    }

    internal static async Task WaitForModifiersReleaseAsync(CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ModifierReleaseMaxWaitMs)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!IsAnyBlockingModifierPressed())
            {
                return;
            }

            await Task.Delay(PollIntervalMs, cancellationToken).ConfigureAwait(false);
        }
    }

    internal static bool IsAnyBlockingModifierPressed() =>
        IsKeyPressed(VIRTUAL_KEY.VK_MENU) ||
        IsKeyPressed(VIRTUAL_KEY.VK_LMENU) ||
        IsKeyPressed(VIRTUAL_KEY.VK_RMENU) ||
        IsKeyPressed(VIRTUAL_KEY.VK_SHIFT) ||
        IsKeyPressed(VIRTUAL_KEY.VK_LSHIFT) ||
        IsKeyPressed(VIRTUAL_KEY.VK_RSHIFT) ||
        IsKeyPressed(VIRTUAL_KEY.VK_LWIN) ||
        IsKeyPressed(VIRTUAL_KEY.VK_RWIN);

    internal static bool IsControlPhysicallyPressed() =>
        IsKeyPressed(VIRTUAL_KEY.VK_CONTROL) ||
        IsKeyPressed(VIRTUAL_KEY.VK_LCONTROL) ||
        IsKeyPressed(VIRTUAL_KEY.VK_RCONTROL);

    internal static bool IsKeyPressed(VIRTUAL_KEY key) =>
        (PInvoke.GetAsyncKeyState((int)key) & 0x8000) != 0;

    private static unsafe bool SendCtrlC()
    {
        var userHoldingCtrl = IsControlPhysicallyPressed();
        var inputs = new List<INPUT>(12);

        // Press VK_CONTROL BEFORE releasing any still-held Alt key so Windows masks menu-bar
        // activation and never fires WM_SYSCOMMAND(SC_KEYMENU) in the target application!
        inputs.Add(CreateKeyInput(VIRTUAL_KEY.VK_CONTROL, isKeyUp: false));

        ReleaseIfPressed(inputs, VIRTUAL_KEY.VK_LMENU);
        ReleaseIfPressed(inputs, VIRTUAL_KEY.VK_RMENU);
        ReleaseIfPressed(inputs, VIRTUAL_KEY.VK_MENU);
        ReleaseIfPressed(inputs, VIRTUAL_KEY.VK_LSHIFT);
        ReleaseIfPressed(inputs, VIRTUAL_KEY.VK_RSHIFT);
        ReleaseIfPressed(inputs, VIRTUAL_KEY.VK_SHIFT);
        ReleaseIfPressed(inputs, VIRTUAL_KEY.VK_LWIN);
        ReleaseIfPressed(inputs, VIRTUAL_KEY.VK_RWIN);

        inputs.Add(CreateKeyInput(VIRTUAL_KEY.VK_C, isKeyUp: false));
        inputs.Add(CreateKeyInput(VIRTUAL_KEY.VK_C, isKeyUp: true));

        if (!userHoldingCtrl)
        {
            inputs.Add(CreateKeyInput(VIRTUAL_KEY.VK_CONTROL, isKeyUp: true));
        }

        var span = CollectionsMarshal.AsSpan(inputs);
        return PInvoke.SendInput(span, sizeof(INPUT)) > 0;
    }

    internal static void ReleaseIfPressed(List<INPUT> inputs, VIRTUAL_KEY key)
    {
        if (IsKeyPressed(key))
        {
            inputs.Add(CreateKeyInput(key, isKeyUp: true));
        }
    }

    internal static INPUT CreateKeyInput(VIRTUAL_KEY key, bool isKeyUp) => new()
    {
        type = INPUT_TYPE.INPUT_KEYBOARD,
        Anonymous = new INPUT._Anonymous_e__Union
        {
            ki = new KEYBDINPUT
            {
                wVk = key,
                wScan = 0,
                dwFlags = isKeyUp ? KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP : 0,
                time = 0,
                dwExtraInfo = 0,
            },
        },
    };
}
