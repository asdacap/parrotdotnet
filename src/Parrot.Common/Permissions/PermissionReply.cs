namespace Parrot.Permissions;

internal sealed record PermissionReply(PermissionReplyKind Kind, PermissionDecision Decision, string Reason)
{
    public PermissionReply(PermissionDecision decision, string reason)
        : this(PermissionReplyKind.Decided, decision, reason)
    {
    }

    public static PermissionReply UserAway { get; } = new(PermissionReplyKind.UserAway, PermissionDecision.Reject, string.Empty);
}
