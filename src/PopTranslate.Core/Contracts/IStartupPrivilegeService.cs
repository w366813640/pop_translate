using PopTranslate.Core.Models;

namespace PopTranslate.Core.Contracts;

public interface IStartupPrivilegeService
{
    bool IsRunningAsAdministrator();

    bool IsStandardStartupEnabled();

    void SetStandardStartup(bool enabled, string executablePath);

    bool IsElevatedStartupTaskEnabled();

    Task<PrivilegeOperationResult> SetElevatedStartupTaskAsync(
        bool enabled,
        string executablePath,
        CancellationToken cancellationToken = default);

    bool RestartAsAdministrator(string executablePath, string? arguments = null);
}
