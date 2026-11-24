using System.ComponentModel;
using System.Diagnostics;
using System.Security;
using System.Security.Principal;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using PopTranslate.Core.Contracts;
using PopTranslate.Core.Models;

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

            // Best-effort cleanup of elevated scheduled task so standard & elevated startup never race at logon.
            if (ShouldSyncDefaultMutualExclusion() && IsRunningAsAdministrator())
            {
                TryDeleteElevatedTaskBestEffort();
            }
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

        string? tempXmlPath = null;
        try
        {
            if (!enabled)
            {
                var isCurrentlyEnabled = await IsElevatedStartupTaskEnabledAsync(cancellationToken).ConfigureAwait(false);
                if (!isCurrentlyEnabled)
                {
                    return new PrivilegeOperationResult(
                        Succeeded: true,
                        Message: "Elevated startup task is already disabled.",
                        RequiresElevation: false);
                }

                var deleteArgs = BuildSchtasksDeleteArguments(_elevatedTaskName);
                var (delExitCode, delOut, delErr) = await RunSchtasksAsync(deleteArgs, cancellationToken).ConfigureAwait(false);
                if (delExitCode == 0)
                {
                    return new PrivilegeOperationResult(
                        Succeeded: true,
                        Message: "Elevated auto-start task removed successfully.",
                        RequiresElevation: false);
                }

                return BuildSchtasksFailureResult(delExitCode, delOut, delErr);
            }

            tempXmlPath = Path.Combine(Path.GetTempPath(), $"PopTranslate_Task_{Guid.NewGuid():N}.xml");
            var taskXml = BuildElevatedTaskXml(cleanPath, GetCurrentUserSidOrName());
            await File.WriteAllTextAsync(tempXmlPath, taskXml, Encoding.Unicode, cancellationToken)
                .ConfigureAwait(false);

            var createXmlArgs = BuildSchtasksCreateXmlArguments(_elevatedTaskName, tempXmlPath);
            var (exitCode, stdOut, stdErr) = await RunSchtasksAsync(createXmlArgs, cancellationToken).ConfigureAwait(false);
            if (exitCode == 0)
            {
                // Mutual exclusion: remove HKCU Run entry so Standard and Elevated startup never race at logon.
                TryDeleteStandardStartupBestEffort();

                return new PrivilegeOperationResult(
                    Succeeded: true,
                    Message: "UAC-free elevated auto-start task configured successfully.",
                    RequiresElevation: false);
            }

            return BuildSchtasksFailureResult(exitCode, stdOut, stdErr);
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
        finally
        {
            if (!string.IsNullOrEmpty(tempXmlPath))
            {
                try
                {
                    if (File.Exists(tempXmlPath))
                    {
                        File.Delete(tempXmlPath);
                    }
                }
                catch
                {
                    // Best-effort cleanup of temporary Task XML.
                }
            }
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

    public static string BuildSchtasksCreateXmlArguments(string taskName, string xmlFilePath)
    {
        var safeTask = ValidateTaskName(taskName);
        if (!TrySanitizeExecutablePath(xmlFilePath, out var cleanXmlPath, out var error))
        {
            throw new ArgumentException(error, nameof(xmlFilePath));
        }

        return $"/Create /TN \"{safeTask}\" /XML \"{cleanXmlPath}\" /F";
    }

    public static string BuildSchtasksDeleteArguments(string taskName)
    {
        var safeTask = ValidateTaskName(taskName);
        return $"/Delete /TN \"{safeTask}\" /F";
    }

    public static string BuildElevatedTaskXml(string executablePath, string? userId = null)
    {
        if (!TrySanitizeExecutablePath(executablePath, out var cleanPath, out var error))
        {
            throw new ArgumentException(error, nameof(executablePath));
        }

        var escapedCommand = SecurityElement.Escape($"\"{cleanPath}\"");
        var workingDir = Path.GetDirectoryName(cleanPath);
        var workingDirElement = !string.IsNullOrWhiteSpace(workingDir)
            ? $"\n      <WorkingDirectory>{SecurityElement.Escape(workingDir)}</WorkingDirectory>"
            : string.Empty;

        var resolvedUser = string.IsNullOrWhiteSpace(userId) ? GetCurrentUserSidOrName() : userId.Trim();
        var userIdElement = !string.IsNullOrWhiteSpace(resolvedUser)
            ? $"\n      <UserId>{SecurityElement.Escape(resolvedUser)}</UserId>"
            : string.Empty;

        return
            $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>{userIdElement}
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">{userIdElement}
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>false</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <WakeToRun>false</WakeToRun>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{escapedCommand}</Command>
                  <Arguments>--minimized</Arguments>{workingDirElement}
                </Exec>
              </Actions>
            </Task>
            """;
    }

    private async Task<bool> IsElevatedStartupTaskEnabledAsync(CancellationToken cancellationToken)
    {
        try
        {
            var args = BuildSchtasksQueryArguments(_elevatedTaskName);
            var (exitCode, _, _) = await RunSchtasksAsync(args, cancellationToken).ConfigureAwait(false);
            return exitCode == 0;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private bool ShouldSyncDefaultMutualExclusion() =>
        string.Equals(_registryValueName, DefaultRegistryValueName, StringComparison.OrdinalIgnoreCase) ||
        !string.Equals(_elevatedTaskName, DefaultElevatedTaskName, StringComparison.OrdinalIgnoreCase);

    private void TryDeleteStandardStartupBestEffort()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(_runRegistrySubKey, writable: true);
            key?.DeleteValue(_registryValueName, throwOnMissingValue: false);
        }
        catch
        {
            // Best-effort cleanup of standard HKCU Run value.
        }
    }

    private void TryDeleteElevatedTaskBestEffort()
    {
        try
        {
            var deleteArgs = BuildSchtasksDeleteArguments(_elevatedTaskName);
            _ = RunSchtasksSync(deleteArgs);
        }
        catch
        {
            // Best-effort cleanup of elevated scheduled task.
        }
    }

    private static PrivilegeOperationResult BuildSchtasksFailureResult(int exitCode, string stdOut, string stdErr)
    {
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

    private static string? GetCurrentUserSidOrName()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity.User?.Value ?? identity.Name;
        }
        catch
        {
            return null;
        }
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
