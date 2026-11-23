using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using PopTranslate.Core.Contracts;
using PopTranslate.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;

namespace PopTranslate.Win32.Startup;

public sealed class StartupPrivilegeService : IStartupPrivilegeService
{
    public const string DefaultRunRegistrySubKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string DefaultRegistryValueName = "PopTranslate";
    public const string DefaultElevatedTaskName = "PopTranslateElevatedAutoStart";

    private readonly string _runRegistrySubKey;
    private readonly string _registryValueName;
    private readonly string _elevatedTaskName;
    private readonly Func<bool> _elevationChecker;

    [ActivatorUtilitiesConstructor]
    public StartupPrivilegeService()
        : this(DefaultRunRegistrySubKey, DefaultRegistryValueName, DefaultElevatedTaskName, null)
    {
    }

    public StartupPrivilegeService(
        string? runRegistrySubKey = null,
        string? registryValueName = null,
        string? elevatedTaskName = null,
        Func<bool>? elevationChecker = null)
    {
        _runRegistrySubKey = string.IsNullOrWhiteSpace(runRegistrySubKey)
            ? DefaultRunRegistrySubKey
            : runRegistrySubKey.Trim();
        _registryValueName = string.IsNullOrWhiteSpace(registryValueName)
            ? DefaultRegistryValueName
            : registryValueName.Trim();
        _elevatedTaskName = string.IsNullOrWhiteSpace(elevatedTaskName)
            ? DefaultElevatedTaskName
            : elevatedTaskName.Trim();
        _elevationChecker = elevationChecker ?? CheckCurrentProcessElevation;
    }

    public bool IsRunningAsAdministrator() => _elevationChecker();

    public bool IsStandardStartupEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(_runRegistrySubKey, writable: false);
            var value = key?.GetValue(_registryValueName) as string;
            return !string.IsNullOrWhiteSpace(value);
        }
        catch
        {
            return false;
        }
    }

    public void SetStandardStartup(bool enabled, string executablePath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(_runRegistrySubKey, writable: true)
            ?? throw new InvalidOperationException($"Unable to open HKCU\\{_runRegistrySubKey} for writing.");

        if (enabled)
        {
            var formatted = FormatStandardStartupValue(executablePath);
            key.SetValue(_registryValueName, formatted, RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(_registryValueName, throwOnMissingValue: false);
        }
    }

    public bool IsElevatedStartupTaskEnabled()
    {
        try
        {
            var args = BuildSchtasksQueryArguments(_elevatedTaskName);
            var (exitCode, _, _) = RunSchtasksSync(args);
            return exitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public async Task<PrivilegeOperationResult> SetElevatedStartupTaskAsync(
        bool enabled,
        string executablePath,
        CancellationToken cancellationToken = default)
    {
        var cleanPath = string.Empty;
        if (enabled && !TrySanitizeExecutablePath(executablePath, out cleanPath, out var validationError))
        {
            return new PrivilegeOperationResult(
                Succeeded: false,
                Message: validationError!,
                RequiresElevation: false);
        }

        if (!IsRunningAsAdministrator())
        {
            return new PrivilegeOperationResult(
                Succeeded: false,
                Message: "Administrator privileges are required to configure the UAC-free elevated startup task. Please restart PopTranslate as Administrator first.",
                RequiresElevation: true);
        }

        try
        {
            if (!enabled && !IsElevatedStartupTaskEnabled())
            {
                return new PrivilegeOperationResult(
                    Succeeded: true,
                    Message: "Elevated startup task is already disabled.",
                    RequiresElevation: false);
            }

            var args = enabled
                ? BuildSchtasksCreateArguments(_elevatedTaskName, cleanPath)
                : BuildSchtasksDeleteArguments(_elevatedTaskName);

            var (exitCode, stdOut, stdErr) = await RunSchtasksAsync(args, cancellationToken).ConfigureAwait(false);
            if (exitCode == 0)
            {
                return new PrivilegeOperationResult(
                    Succeeded: true,
                    Message: enabled
                        ? "UAC-free elevated auto-start task configured successfully."
                        : "Elevated auto-start task removed successfully.",
                    RequiresElevation: false);
            }

            var detail = !string.IsNullOrWhiteSpace(stdErr) ? stdErr.Trim() : stdOut.Trim();
            if (string.IsNullOrWhiteSpace(detail))
            {
                detail = $"schtasks.exe exited with code {exitCode}.";
            }

            return new PrivilegeOperationResult(
                Succeeded: false,
                Message: $"Failed to update elevated startup task: {detail}",
                RequiresElevation: false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new PrivilegeOperationResult(
                Succeeded: false,
                Message: $"Failed to execute schtasks.exe: {ex.Message}",
                RequiresElevation: false);
        }
    }

    public bool RestartAsAdministrator(string executablePath, string? arguments = null)
    {
        if (!TrySanitizeExecutablePath(executablePath, out var cleanPath, out _))
        {
            return false;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = cleanPath,
                Arguments = string.IsNullOrWhiteSpace(arguments) ? "--elevated" : arguments.Trim(),
                UseShellExecute = true,
                Verb = "runas",
            };

            using var process = Process.Start(startInfo);
            return process is not null;
        }
        catch (Win32Exception)
        {
            // User declined UAC prompt (ERROR_CANCELLED = 1223) or executable cannot be launched.
            return false;
        }
        catch
        {
            return false;
        }
    }

    public static string FormatStandardStartupValue(string executablePath)
    {
        if (!TrySanitizeExecutablePath(executablePath, out var cleanPath, out var error))
        {
            throw new ArgumentException(error, nameof(executablePath));
        }

        return $"\"{cleanPath}\" --minimized";
    }

    public static string BuildSchtasksQueryArguments(string taskName)
    {
        var safeTask = ValidateTaskName(taskName);
        return $"/Query /TN \"{safeTask}\"";
    }

    public static string BuildSchtasksCreateArguments(string taskName, string executablePath)
    {
        var safeTask = ValidateTaskName(taskName);
        if (!TrySanitizeExecutablePath(executablePath, out var cleanPath, out var error))
        {
            throw new ArgumentException(error, nameof(executablePath));
        }

        return $"/Create /TN \"{safeTask}\" /TR \"\\\"{cleanPath}\\\" --minimized\" /SC ONLOGON /RL HIGHEST /F";
    }

    public static string BuildSchtasksDeleteArguments(string taskName)
    {
        var safeTask = ValidateTaskName(taskName);
        return $"/Delete /TN \"{safeTask}\" /F";
    }

    private static bool CheckCurrentProcessElevation()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private static string ValidateTaskName(string taskName)
    {
        if (string.IsNullOrWhiteSpace(taskName))
        {
            throw new ArgumentException("Task name cannot be empty.", nameof(taskName));
        }

        var trimmed = taskName.Trim();
        if (trimmed.Contains('"') || trimmed.Any(char.IsControl))
        {
            throw new ArgumentException("Task name contains invalid characters.", nameof(taskName));
        }

        return trimmed;
    }

    private static bool TrySanitizeExecutablePath(string? executablePath, out string cleanPath, out string? error)
    {
        cleanPath = string.Empty;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            error = "Executable path cannot be empty.";
            return false;
        }

        var trimmed = executablePath.Trim().Trim('"').Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            error = "Executable path cannot be empty.";
            return false;
        }

        if (trimmed.Contains('"') || trimmed.Any(char.IsControl))
        {
            error = "Executable path contains invalid characters.";
            return false;
        }

        cleanPath = trimmed;
        error = null;
        return true;
    }

    private static (int ExitCode, string StdOut, string StdErr) RunSchtasksSync(string arguments)
    {
        using var process = CreateSchtasksProcess(arguments);
        process.Start();
        var stdOut = process.StandardOutput.ReadToEnd();
        var stdErr = process.StandardError.ReadToEnd();
        process.WaitForExit(10_000);
        return (process.ExitCode, stdOut, stdErr);
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunSchtasksAsync(
        string arguments,
        CancellationToken cancellationToken)
    {
        using var process = CreateSchtasksProcess(arguments);
        process.Start();

        var stdOutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stdErrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var stdOut = await stdOutTask.ConfigureAwait(false);
        var stdErr = await stdErrTask.ConfigureAwait(false);

        return (process.ExitCode, stdOut, stdErr);
    }

    private static Process CreateSchtasksProcess(string arguments) => new()
    {
        StartInfo = new ProcessStartInfo
        {
            FileName = "schtasks.exe",
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        },
    };
}
