using Parrot.Security;

namespace Parrot.Permissions;

internal sealed record PermissionPending(
    string Id,
    string AgentSessionId,
    string Reason,
    IReadOnlyList<SecurityWriteTarget> Targets,
    IReadOnlyList<PermissionChoice> Choices);
