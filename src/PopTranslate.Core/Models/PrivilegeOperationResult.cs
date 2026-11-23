namespace PopTranslate.Core.Models;

public sealed record PrivilegeOperationResult(
    bool Succeeded,
    string Message,
    bool RequiresElevation = false);
