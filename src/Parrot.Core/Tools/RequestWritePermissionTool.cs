using System.Text.Json;
using System.Text.Json.Serialization;
using Parrot.Agent;
using Parrot.Permissions;
using Parrot.Security;

namespace Parrot.Tools;

internal sealed class RequestWritePermissionTool(
    AgentIdentity identity,
    AgentSessionSecurity security,
    IPermissionBroker broker) : ITool
{
    public string Name => "request_write_permission";

    public async Task<ToolExecutionResult> Execute(
        ToolInvocation invocation,
        AgentTurnSelection selection,
        CancellationToken cancellationToken)
    {
        try
        {
            var input = JsonSerializer.Deserialize(
                invocation.ArgumentsJson,
                RequestWritePermissionJsonContext.Default.RequestWritePermissionToolInput)
                ?? throw new FormatException("Tool arguments must be an object.");
            var paths = input.Paths ?? throw new FormatException("Tool arguments require an array 'paths'.");
            var reason = input.Reason?.Trim() ?? throw new FormatException("Tool arguments require a string 'reason'.");
            if (paths.Length == 0)
            {
                throw new FormatException("Tool arguments require at least one path.");
            }

            if (reason.Length == 0)
            {
                throw new FormatException("Permission reason must not be empty.");
            }

            var targets = paths.Select(SecurityWriteTarget.Resolve).Distinct().ToArray();
            if (targets.All(target => selection.SecurityProfile.AllowsWrite(target.Path)))
            {
                return $"Write permission already allowed by the current security profile: {string.Join(", ", targets.Select(target => target.Path))}";
            }

            if (selection.SecurityProfile.ReadOnly)
            {
                throw new PermissionException("request_write_permission is not permitted by the current security profile");
            }

            var reply = await broker.Request(identity, security, reason, targets, cancellationToken).ConfigureAwait(false);
            if (reply.Kind == PermissionReplyKind.UserAway)
            {
                return ToolResultFormatter.Text(invocation, "The user is away.");
            }

            if (reply.Decision == PermissionDecision.Grant)
            {
                return $"The agent session security profile now allows writing: {string.Join(", ", targets.Select(target => target.Path))}";
            }

            return reply.Reason.Length == 0
                ? "Write permission request rejected."
                : $"Write permission request rejected: {reply.Reason}";
        }
        catch (Exception failure) when (failure is JsonException or FormatException or ArgumentException or IOException
                                        or UnauthorizedAccessException or PermissionException)
        {
            return ToolResultFormatter.Error(invocation, failure.Message);
        }
    }

    internal sealed class Input
    {
        [JsonPropertyName("paths")]
        public string[]? Paths { get; init; }

        [JsonPropertyName("reason")]
        public string? Reason { get; init; }
    }
}
