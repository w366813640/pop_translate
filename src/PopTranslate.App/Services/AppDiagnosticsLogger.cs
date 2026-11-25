using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using PopTranslate.Core.Configuration;

namespace PopTranslate.App.Services;

/// <summary>
/// Privacy-safe rolling diagnostics logger for startup, crash, and system lifecycle events.
/// Never logs user source/translated text or plaintext API credentials.
/// </summary>
public static partial class AppDiagnosticsLogger
{
    private const long MaxLogBytes = 2 * 1024 * 1024; // 2 MB
    private const int MaxMessageLength = 1024;
    private const int MaxStackTraceLength = 4096;

    private static readonly Lock SyncRoot = new();

    public static bool IsPortableMode =>
        JsonSettingsStore.IsPortableMode();

    public static string LogDirectoryPath =>
        JsonSettingsStore.ResolvePortableRootDirectory() is { } portableRoot
            ? Path.Combine(portableRoot, "data", "logs")
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PopTranslate",
                "logs");

    public static string LogFilePath => Path.Combine(LogDirectoryPath, "app.log");

    public static void Info(string category, string message) =>
        WriteEntry("INFO", category, message, exception: null);

    public static void Warn(string category, string message, Exception? exception = null) =>
        WriteEntry("WARN", category, message, exception);

    public static void Error(string category, string message, Exception? exception = null) =>
        WriteEntry("ERROR", category, message, exception);

    private static void WriteEntry(string level, string category, string message, Exception? exception)
    {
        try
        {
            var safeCategory = string.IsNullOrWhiteSpace(category) ? "General" : category.Trim();
            var safeMessage = ScrubSensitiveData(message, MaxMessageLength);
            var timestamp = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz");
            var pid = Environment.ProcessId;

            var builder = new StringBuilder(256);
            builder.Append('[')
                .Append(timestamp)
                .Append("] [")
                .Append(level)
                .Append("] [PID:")
                .Append(pid)
                .Append("] [")
                .Append(safeCategory)
                .Append("] ")
                .AppendLine(safeMessage);

            if (exception is not null)
            {
                AppendSanitizedException(builder, exception, depth: 0);
            }

            var line = builder.ToString();
            Debug.Write(line);

            lock (SyncRoot)
            {
                var logDir = LogDirectoryPath;
                Directory.CreateDirectory(logDir);

                var logFile = Path.Combine(logDir, "app.log");
                RollLogFileIfNeeded(logFile);

                File.AppendAllText(logFile, line, Encoding.UTF8);
            }
        }
        catch
        {
            // Logging must never throw or interfere with application execution.
        }
    }

    private static void AppendSanitizedException(StringBuilder builder, Exception ex, int depth)
    {
        if (depth > 4)
        {
            return;
        }

        var indent = depth == 0 ? "  " : "    ";
        builder.Append(indent)
            .Append(ex.GetType().FullName)
            .Append(" (0x")
            .Append(ex.HResult.ToString("X8"))
            .Append("): ")
            .AppendLine(ScrubSensitiveData(ex.Message, MaxMessageLength));

        if (!string.IsNullOrWhiteSpace(ex.StackTrace))
        {
            builder.AppendLine(ScrubSensitiveData(ex.StackTrace, MaxStackTraceLength));
        }

        if (ex is AggregateException agg)
        {
            foreach (var inner in agg.InnerExceptions.Take(3))
            {
                AppendSanitizedException(builder, inner, depth + 1);
            }
        }
        else if (ex.InnerException is not null)
        {
            AppendSanitizedException(builder, ex.InnerException, depth + 1);
        }
    }

    private static void RollLogFileIfNeeded(string logFile)
    {
        try
        {
            var info = new FileInfo(logFile);
            if (!info.Exists || info.Length < MaxLogBytes)
            {
                return;
            }

            var backupFile = Path.Combine(info.DirectoryName ?? LogDirectoryPath, "app.1.log");
            File.Move(logFile, backupFile, overwrite: true);
        }
        catch
        {
            // Ignore rolling contention; append will still proceed if possible.
        }
    }

    private static string ScrubSensitiveData(string? input, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        var scrubbed = SecretPatternRegex().Replace(input, match =>
        {
            var value = match.Value;
            if (value.StartsWith("Bearer", StringComparison.OrdinalIgnoreCase))
            {
                return "Bearer [REDACTED]";
            }

            if (value.StartsWith("DeepL-Auth-Key", StringComparison.OrdinalIgnoreCase))
            {
                return "DeepL-Auth-Key [REDACTED]";
            }

            if (value.StartsWith("dpapi:", StringComparison.OrdinalIgnoreCase))
            {
                return "dpapi:[REDACTED]";
            }

            if (value.StartsWith("key=", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("api_key=", StringComparison.OrdinalIgnoreCase))
            {
                var eqIdx = value.IndexOf('=');
                return string.Concat(value.AsSpan(0, eqIdx + 1), "[REDACTED]");
            }

            return "[REDACTED_SECRET]";
        });

        if (scrubbed.Length > maxLength)
        {
            return string.Concat(scrubbed.AsSpan(0, maxLength), "...(truncated)");
        }

        return scrubbed;
    }

    [GeneratedRegex(
        @"(?:Bearer\s+[A-Za-z0-9\-._~+/]+=*|DeepL-Auth-Key\s+[^\s""']+|dpapi:[A-Za-z0-9+/=]+|sk-[A-Za-z0-9\-_]{10,}|(?:api_)?key=[A-Za-z0-9\-._~]{8,})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretPatternRegex();
}
