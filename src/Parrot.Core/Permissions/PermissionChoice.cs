namespace Parrot.Permissions;

internal sealed record PermissionChoice(
    string Value,
    string Label,
    PermissionDecision Decision,
    bool RequiresReason);
