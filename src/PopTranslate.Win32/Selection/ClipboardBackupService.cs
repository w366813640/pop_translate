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

public sealed class ClipboardSnapshot(IReadOnlyList<ClipboardFormatEntry> entries)
{
    public static readonly ClipboardSnapshot Empty = new([]);

    public IReadOnlyList<ClipboardFormatEntry> Entries { get; } = entries;

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

    private readonly Lazy<AntiPollutionClipboardFormatIds> _antiPollutionIds;

    public ClipboardBackupService()
    {
        _antiPollutionIds = new Lazy<AntiPollutionClipboardFormatIds>(RegisterAntiPollutionFormats);
    }

    public AntiPollutionClipboardFormatIds GetAntiPollutionFormatIds() => _antiPollutionIds.Value;

    public bool IsSafeHGlobalFormat(uint format)
    {
        var ids = GetAntiPollutionFormatIds();
        if (format == 0 ||
            format == ids.ExcludeFromMonitorProcessing ||
            format == ids.CanIncludeInClipboardHistory ||
            format == ids.CanUploadToCloudClipboard)
        {
            return false;
        }

        if (format is CfText or CfOemText or CfDib or CfUnicodeText or CfHDrop or CfLocale or CfDibV5)
        {
            return true;
        }

        return format >= RegisteredFormatStart && format <= RegisteredFormatEnd;
    }

    public unsafe ClipboardSnapshot CaptureSnapshot()
    {
        if (!TryOpenClipboardWithRetry())
        {
            return ClipboardSnapshot.Empty;
        }

        try
        {
            var entries = new List<ClipboardFormatEntry>();
            uint format = 0;

            while ((format = PInvoke.EnumClipboardFormats(format)) != 0)
            {
                if (!IsSafeHGlobalFormat(format))
                {
                    continue;
                }

                var handle = PInvoke.GetClipboardData(format);
                if (handle.IsNull)
                {
                    continue;
                }

                var hGlobal = new HGLOBAL(handle.Value);
                var size = PInvoke.GlobalSize(hGlobal);
                if (size == 0 || size > MaxBackupPayloadBytes)
                {
                    continue;
                }

                var ptr = PInvoke.GlobalLock(hGlobal);
                if (ptr is null)
                {
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

            return entries.Count == 0 ? ClipboardSnapshot.Empty : new ClipboardSnapshot(entries);
        }
        finally
        {
            PInvoke.CloseClipboard();
        }
    }

    public unsafe bool RestoreSnapshot(ClipboardSnapshot snapshot, bool includeAntiPollutionMarkers = true)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (!TryOpenClipboardWithRetry())
        {
            return false;
        }

        try
        {
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
        finally
        {
            PInvoke.CloseClipboard();
        }
    }

    public unsafe string? TryReadUnicodeText()
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
        finally
        {
            PInvoke.CloseClipboard();
        }
    }

    private static AntiPollutionClipboardFormatIds RegisterAntiPollutionFormats()
    {
        var excludeMonitor = PInvoke.RegisterClipboardFormat(ExcludeFromMonitorProcessingFormatName);
        var canIncludeHistory = PInvoke.RegisterClipboardFormat(CanIncludeInClipboardHistoryFormatName);
        var canUploadCloud = PInvoke.RegisterClipboardFormat(CanUploadToCloudClipboardFormatName);

        return new AntiPollutionClipboardFormatIds(excludeMonitor, canIncludeHistory, canUploadCloud);
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

            Thread.Sleep(delayMs);
        }

        return false;
    }
}
