using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace PopTranslate.Win32.Selection;

public class ClipboardFallbackSelectionReader
{
    private const int ModifierReleaseMaxWaitMs = 150;
    private const int PollIntervalMs = 10;

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
            var snapshot = _backupService.CaptureSnapshot();
            var beforeSequence = PInvoke.GetClipboardSequenceNumber();

            try
            {
                await WaitForModifiersReleaseAsync(cancellationToken).ConfigureAwait(false);
                SendCtrlC();

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

                var capturedText = _backupService.TryReadUnicodeText();
                return string.IsNullOrWhiteSpace(capturedText) ? null : capturedText;
            }
            finally
            {
                if (PInvoke.GetClipboardSequenceNumber() != beforeSequence)
                {
                    _backupService.RestoreSnapshot(snapshot, includeAntiPollutionMarkers: true);
                }
            }
        }
        finally
        {
            _captureGate.Release();
        }
    }

    private static async Task WaitForModifiersReleaseAsync(CancellationToken cancellationToken)
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

    private static bool IsAnyBlockingModifierPressed() =>
        IsKeyPressed(VIRTUAL_KEY.VK_MENU) ||
        IsKeyPressed(VIRTUAL_KEY.VK_LMENU) ||
        IsKeyPressed(VIRTUAL_KEY.VK_RMENU) ||
        IsKeyPressed(VIRTUAL_KEY.VK_SHIFT) ||
        IsKeyPressed(VIRTUAL_KEY.VK_LSHIFT) ||
        IsKeyPressed(VIRTUAL_KEY.VK_RSHIFT) ||
        IsKeyPressed(VIRTUAL_KEY.VK_LWIN) ||
        IsKeyPressed(VIRTUAL_KEY.VK_RWIN);

    private static bool IsKeyPressed(VIRTUAL_KEY key) =>
        (PInvoke.GetAsyncKeyState((int)key) & 0x8000) != 0;

    private static unsafe void SendCtrlC()
    {
        var inputs = new List<INPUT>(10);

        // Release any still-held conflicting hotkey modifiers before injecting Ctrl+C.
        ReleaseIfPressed(inputs, VIRTUAL_KEY.VK_LMENU);
        ReleaseIfPressed(inputs, VIRTUAL_KEY.VK_RMENU);
        ReleaseIfPressed(inputs, VIRTUAL_KEY.VK_LSHIFT);
        ReleaseIfPressed(inputs, VIRTUAL_KEY.VK_RSHIFT);
        ReleaseIfPressed(inputs, VIRTUAL_KEY.VK_LWIN);
        ReleaseIfPressed(inputs, VIRTUAL_KEY.VK_RWIN);

        inputs.Add(CreateKeyInput(VIRTUAL_KEY.VK_CONTROL, isKeyUp: false));
        inputs.Add(CreateKeyInput(VIRTUAL_KEY.VK_C, isKeyUp: false));
        inputs.Add(CreateKeyInput(VIRTUAL_KEY.VK_C, isKeyUp: true));
        inputs.Add(CreateKeyInput(VIRTUAL_KEY.VK_CONTROL, isKeyUp: true));

        var span = CollectionsMarshal.AsSpan(inputs);
        PInvoke.SendInput(span, sizeof(INPUT));
    }

    private static void ReleaseIfPressed(List<INPUT> inputs, VIRTUAL_KEY key)
    {
        if (IsKeyPressed(key))
        {
            inputs.Add(CreateKeyInput(key, isKeyUp: true));
        }
    }

    private static INPUT CreateKeyInput(VIRTUAL_KEY key, bool isKeyUp) => new()
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
