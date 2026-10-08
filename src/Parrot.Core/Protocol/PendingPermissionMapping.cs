using Parrot.Permissions;
using Parrot.Security;

namespace Parrot.Protocol;

internal static class PendingPermissionMapping
{
    internal static PendingPermission Map(PermissionPending request)
    {
        var pending = new PendingPermission
        {
            Id = request.Id,
            AgentSessionId = request.AgentSessionId,
            Reason = request.Reason,
        };
        pending.Targets.AddRange(request.Targets.Select(target => new PermissionTarget
        {
            Kind = target.Kind == SecurityWriteTargetKind.File
                ? PermissionTargetKind.File
                : PermissionTargetKind.Directory,
            Scope = PermissionTargetScope.Write,
            Path = target.Path,
        }));
        pending.Choices.AddRange(request.Choices.Select(choice => new PermissionChoice
        {
            Value = choice.Value,
            Label = choice.Label,
            Action = choice.Decision == PermissionDecision.Grant
                ? PermissionAction.Allow
                : PermissionAction.Deny,
            RequiresReason = choice.RequiresReason,
        }));
        return pending;
    }
}
