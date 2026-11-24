using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Memory;

namespace PopTranslate.Win32.Selection;

public sealed record AntiPollutionClipboardFormatIds(
    uint ExcludeFromMonitorProcessing,
    uint CanIncludeInClipboardHistory,
    uint CanUploadToCloudClipboard);

public sealed record ClipboardFormatEntry(uint Format, byte[] Data);

public sealed class ClipboardSnapshot(
    IReadOnlyList<ClipboardFormatEntry> entries,
    bool captureSucceeded = true)
{
    public static readonly ClipboardSnapshot Empty = new([], captureSucceeded: true);

    public static readonly ClipboardSnapshot Failed = new([], captureSucceeded: false);

    public IReadOnlyList<ClipboardFormatEntry> Entries { get; } = entries;

    public bool CaptureSucceeded { get; } = captureSucceeded;

    public bool IsEmpty => Entries.Count == 0;
}

public class ClipboardBackupService
{
    public const uint CfText = 1;
    public const uint CfOemText = 7;
    public const uint CfDib = 8;
    public const uint CfUnicodeText = 13;
    public const uint CfHDrop = 15;
    public const uint CfLocale = 16;
    public const uint CfDibV5 = 17;

    private const uint RegisteredFormatStart = 0xC000;
    private const uint RegisteredFormatEnd = 0xFFFF;
    private const nuint MaxBackupPayloadBytes = 32 * 1024 * 1024; // 32 MB safety cap per format

    public const string ExcludeFromMonitorProcessingFormatName = "ExcludeClipboardContentFromMonitorProcessing";
    public const string CanIncludeInClipboardHistoryFormatName = "CanIncludeInClipboardHistory";
    public const string CanUploadToCloudClipboardFormatName = "CanUploadToCloudClipboard";

    public static readonly IReadOnlyList<string> AntiPollutionFormatNames =
    [
        ExcludeFromMonitorProcessingFormatName,
        CanIncludeInClipboardHistoryFormatName,
        CanUploadToCloudClipboardFormatName,
    ];

    private static readonly string[] SafeRegisteredFormatNames =
    [
        "HTML Format",
        "Rich Text Format",
        "Rich Text Format Without Objects",
        "PNG",
        "JFIF",
        "GIF",
        "CF_RTF",
        "Csv",
        "text/html",
        "text/plain",
    ];

    private readonly Lazy<AntiPollutionClipboardFormatIds> _antiPollutionIds;
    private readonly Lazy<HashSet<uint>> _whitelistedRegisteredFormatIds;

    public ClipboardBackupService()
    {
        _antiPollutionIds = new Lazy<AntiPollutionClipboardFormatIds>(RegisterAntiPollutionFormats);
        _whitelistedRegisteredFormatIds = new Lazy<HashSet<uint>>(RegisterWhitelistedFormats);
    }

    public AntiPollutionClipboardFormatIds GetAntiPollutionFormatIds() => _antiPollutionIds.Value;

    public bool IsSafeHGlobalFormat(uint format)
    {
        if (IsAntiPollutionFormat(format) || format == 0)
        {
            return false;
        }

        if (IsStandardSafeHGlobalFormat(format))
        {
            return true;
        }

        return format >= RegisteredFormatStart && format <= RegisteredFormatEnd;
    }

    public bool IsWhitelistedBackupFormat(uint format)
    {
        if (IsAntiPollutionFormat(format) || format == 0)
        {
            return false;
        }

        if (IsStandardSafeHGlobalFormat(format))
        {
            return true;
        }

        return _whitelistedRegisteredFormatIds.Value.Contains(format);
    }

    public ClipboardSnapshot CaptureSnapshot()
    {
        if (!TryOpenClipboardWithRetry())
        {
            return ClipboardSnapshot.Failed;
        }

        try
        {
            return CaptureOpenedClipboardSnapshot();
        }
        finally
        {
            PInvoke.CloseClipboard();
        }
    }

    public async Task<ClipboardSnapshot> CaptureSnapshotAsync(CancellationToken cancellationToken = default)
    {
        if (!await TryOpenClipboardAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            return ClipboardSnapshot.Failed;
        }

        try
        {
            return CaptureOpenedClipboardSnapshot();
        }
        finally
        {
            PInvoke.CloseClipboard();
        }
    }

    public bool RestoreSnapshot(ClipboardSnapshot snapshot, bool includeAntiPollutionMarkers = true)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!snapshot.CaptureSucceeded)
        {
            return false;
        }

        if (!TryOpenClipboardWithRetry())
        {
            return false;
        }

        try
        {
            return RestoreOpenedClipboardSnapshot(snapshot, includeAntiPollutionMarkers);
        }
        finally
        {
            PInvoke.CloseClipboard();
        }
    }

    public async Task<bool> RestoreSnapshotAsync(
        ClipboardSnapshot snapshot,
        bool includeAntiPollutionMarkers = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!snapshot.CaptureSucceeded)
        {
            return false;
        }

        if (!await TryOpenClipboardAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        try
        {
            return RestoreOpenedClipboardSnapshot(snapshot, includeAntiPollutionMarkers);
        }
        finally
        {
            PInvoke.CloseClipboard();
        }
    }

    public string? TryReadUnicodeText()
    {
        if (!PInvoke.IsClipboardFormatAvailable(CfUnicodeText))
        {
            return null;
        }

        if (!TryOpenClipboardWithRetry())
        {
            return null;
        }

        try
        {
            return ReadOpenedUnicodeText();
        }
        finally
        {
            PInvoke.CloseClipboard();
        }
    }

    public async Task<string?> TryReadUnicodeTextAsync(CancellationToken cancellationToken = default)
    {
        if (!PInvoke.IsClipboardFormatAvailable(CfUnicodeText))
        {
            return null;
        }

        if (!await TryOpenClipboardAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        try
        {
            return ReadOpenedUnicodeText();
        }
        finally
        {
            PInvoke.CloseClipboard();
        }
    }

    public async Task<(string? Text, bool Restored)> TryReadUnicodeTextAndRestoreAsync(
        ClipboardSnapshot snapshot,
        bool includeAntiPollutionMarkers = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (!await TryOpenClipboardAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            return (null, false);
        }

        try
        {
            var text = PInvoke.IsClipboardFormatAvailable(CfUnicodeText)
                ? ReadOpenedUnicodeText()
                : null;

            var restored = snapshot.CaptureSucceeded &&
                           RestoreOpenedClipboardSnapshot(snapshot, includeAntiPollutionMarkers);

            return (text, restored);
        }
        finally
        {
            PInvoke.CloseClipboard();
        }
    }

    public async Task<bool> TryWriteUnicodeTextAsync(
        string text,
        bool includeAntiPollutionMarkers = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (!await TryOpenClipboardAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        try
        {
            if (!PInvoke.EmptyClipboard())
            {
                return false;
            }

            if (!TrySetOpenedUnicodeText(text))
            {
                return false;
            }

            if (includeAntiPollutionMarkers)
            {
                WriteAntiPollutionMarkersWhileOpen();
            }

            return true;
        }
        finally
        {
            PInvoke.CloseClipboard();
        }
    }

    public static async Task<bool> TryOpenClipboardAsync(
        int maxAttempts = 8,
        int delayMs = 10,
        CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (PInvoke.OpenClipboard(HWND.Null))
            {
                return true;
            }

            if (attempt + 1 < maxAttempts)
            {
                await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
            }
        }

        return false;
    }

    private unsafe ClipboardSnapshot CaptureOpenedClipboardSnapshot()
    {
        var entries = new List<ClipboardFormatEntry>();
        var sawNonMarkerFormat = false;
        var hadFailedOrOversizedFormat = false;
        uint format = 0;

        while ((format = PInvoke.EnumClipboardFormats(format)) != 0)
        {
            if (IsAntiPollutionFormat(format))
            {
                continue;
            }

            if (format != CfLocale)
            {
                sawNonMarkerFormat = true;
            }

            if (!IsWhitelistedBackupFormat(format))
            {
                continue;
            }

            var handle = PInvoke.GetClipboardData(format);
            if (handle.IsNull)
            {
                hadFailedOrOversizedFormat = true;
                continue;
            }

            var hGlobal = new HGLOBAL(handle.Value);
            var size = PInvoke.GlobalSize(hGlobal);
            if (size == 0 || size > MaxBackupPayloadBytes)
            {
                hadFailedOrOversizedFormat = true;
                continue;
            }

            var ptr = PInvoke.GlobalLock(hGlobal);
            if (ptr is null)
            {
                hadFailedOrOversizedFormat = true;
                continue;
            }

            try
            {
                var buffer = new byte[(int)size];
                Marshal.Copy((nint)ptr, buffer, 0, buffer.Length);
                entries.Add(new ClipboardFormatEntry(format, buffer));
            }
            finally
            {
                PInvoke.GlobalUnlock(hGlobal);
            }
        }

        if (hadFailedOrOversizedFormat)
        {
            return ClipboardSnapshot.Failed;
        }

        var hasSubstantiveEntry = entries.Any(e => e.Format != CfLocale);
        if (!hasSubstantiveEntry && sawNonMarkerFormat)
        {
            // Clipboard holds un-backupable content (e.g. CF_BITMAP-only, CF_ENHMETAFILE, or OLE DataObject).
            // Mark CaptureSucceeded = false so callers never wipe it via EmptyClipboard().
            return ClipboardSnapshot.Failed;
        }

        return entries.Count == 0
            ? ClipboardSnapshot.Empty
            : new ClipboardSnapshot(entries, captureSucceeded: true);
    }

    private bool RestoreOpenedClipboardSnapshot(ClipboardSnapshot snapshot, bool includeAntiPollutionMarkers)
    {
        if (!snapshot.CaptureSucceeded)
        {
            return false;
        }

        if (!PInvoke.EmptyClipboard())
        {
            return false;
        }

        foreach (var entry in snapshot.Entries)
        {
            if (entry.Data.Length == 0)
            {
                continue;
            }

            TrySetClipboardBytes(entry.Format, entry.Data);
        }

        if (includeAntiPollutionMarkers)
        {
            WriteAntiPollutionMarkersWhileOpen();
        }

        return true;
    }

    private static unsafe string? ReadOpenedUnicodeText()
    {
        var handle = PInvoke.GetClipboardData(CfUnicodeText);
        if (handle.IsNull)
        {
            return null;
        }

        var hGlobal = new HGLOBAL(handle.Value);
        var byteSize = PInvoke.GlobalSize(hGlobal);
        if (byteSize < 2)
        {
            return null;
        }

        var ptr = PInvoke.GlobalLock(hGlobal);
        if (ptr is null)
        {
            return null;
        }

        try
        {
            var maxChars = (int)Math.Min(byteSize / 2, int.MaxValue);
            var span = new ReadOnlySpan<char>(ptr, maxChars);
            var nullIndex = span.IndexOf('\0');
            return nullIndex >= 0 ? new string(span[..nullIndex]) : new string(span);
        }
        finally
        {
            PInvoke.GlobalUnlock(hGlobal);
        }
    }

    private static unsafe bool TrySetOpenedUnicodeText(string text)
    {
        var charCount = checked(text.Length + 1);
        var byteCount = checked((nuint)(charCount * sizeof(char)));

        var hGlobal = PInvoke.GlobalAlloc(GLOBAL_ALLOC_FLAGS.GMEM_MOVEABLE, byteCount);
        if (hGlobal.IsNull)
        {
            return false;
        }

        var locked = (char*)PInvoke.GlobalLock(hGlobal);
        if (locked is null)
        {
            PInvoke.GlobalFree(hGlobal);
            return false;
        }

        try
        {
            var dest = new Span<char>(locked, charCount);
            text.AsSpan().CopyTo(dest);
            dest[text.Length] = '\0';
        }
        finally
        {
            PInvoke.GlobalUnlock(hGlobal);
        }

        var setResult = PInvoke.SetClipboardData(CfUnicodeText, new HANDLE(hGlobal.Value));
        if (setResult.IsNull)
        {
            PInvoke.GlobalFree(hGlobal);
            return false;
        }

        return true;
    }

    private bool IsAntiPollutionFormat(uint format)
    {
        var ids = GetAntiPollutionFormatIds();
        return format != 0 &&
               (format == ids.ExcludeFromMonitorProcessing ||
                format == ids.CanIncludeInClipboardHistory ||
                format == ids.CanUploadToCloudClipboard);
    }

    private static bool IsStandardSafeHGlobalFormat(uint format) =>
        format is CfText or CfOemText or CfDib or CfUnicodeText or CfHDrop or CfLocale or CfDibV5;

    private static AntiPollutionClipboardFormatIds RegisterAntiPollutionFormats()
    {
        var excludeMonitor = PInvoke.RegisterClipboardFormat(ExcludeFromMonitorProcessingFormatName);
        var canIncludeHistory = PInvoke.RegisterClipboardFormat(CanIncludeInClipboardHistoryFormatName);
        var canUploadCloud = PInvoke.RegisterClipboardFormat(CanUploadToCloudClipboardFormatName);

        return new AntiPollutionClipboardFormatIds(excludeMonitor, canIncludeHistory, canUploadCloud);
    }

    private static HashSet<uint> RegisterWhitelistedFormats()
    {
        var set = new HashSet<uint>();
        foreach (var name in SafeRegisteredFormatNames)
        {
            var id = PInvoke.RegisterClipboardFormat(name);
            if (id != 0)
            {
                set.Add(id);
            }
        }

        return set;
    }

    private void WriteAntiPollutionMarkersWhileOpen()
    {
        var ids = GetAntiPollutionFormatIds();
        ReadOnlySpan<byte> zeroDword = [0, 0, 0, 0];

        if (ids.ExcludeFromMonitorProcessing != 0)
        {
            TrySetClipboardBytes(ids.ExcludeFromMonitorProcessing, zeroDword);
        }

        if (ids.CanIncludeInClipboardHistory != 0)
        {
            TrySetClipboardBytes(ids.CanIncludeInClipboardHistory, zeroDword);
        }

        if (ids.CanUploadToCloudClipboard != 0)
        {
            TrySetClipboardBytes(ids.CanUploadToCloudClipboard, zeroDword);
        }
    }

    private static unsafe bool TrySetClipboardBytes(uint format, ReadOnlySpan<byte> data)
    {
        var hGlobal = PInvoke.GlobalAlloc(GLOBAL_ALLOC_FLAGS.GMEM_MOVEABLE, (nuint)data.Length);
        if (hGlobal.IsNull)
        {
            return false;
        }

        var locked = PInvoke.GlobalLock(hGlobal);
        if (locked is null)
        {
            PInvoke.GlobalFree(hGlobal);
            return false;
        }

        try
        {
            data.CopyTo(new Span<byte>(locked, data.Length));
        }
        finally
        {
            PInvoke.GlobalUnlock(hGlobal);
        }

        var setResult = PInvoke.SetClipboardData(format, new HANDLE(hGlobal.Value));
        if (setResult.IsNull)
        {
            PInvoke.GlobalFree(hGlobal);
            return false;
        }

        return true;
    }

    private static bool TryOpenClipboardWithRetry(int maxAttempts = 8, int delayMs = 10)
    {
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            if (PInvoke.OpenClipboard(HWND.Null))
            {
                return true;
            }

            if (attempt + 1 < maxAttempts)
            {
                Thread.Sleep(delayMs);
            }
        }

        return false;
    }
}
