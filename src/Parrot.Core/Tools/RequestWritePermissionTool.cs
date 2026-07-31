using System.Text.Json;
using System.Text.Json.Serialization;
using Parrot.Agent;
using Parrot.Permissions;
using Parrot.Security;

namespace Parrot.Tools;

internal sealed class RequestWritePermissionTool(
    PermissionBroker broker,
    AgentSession agentSession,
    SecurityProfile securityProfile) : ITool
{
    public string Name => "request_write_permission";

    public string Description =>
        "Request agent-session-scoped permission for write, edit, and sandboxed shell operations on exact existing files or directories.";

    public string ParametersJson =>
        """
        {"type":"object","properties":{"paths":{"type":"array","minItems":1,"items":{"type":"string","minLength":1},"description":"Exact absolute paths of existing files or directories to make writable for write, edit, and sandboxed shell operations"},"reason":{"type":"string","minLength":1,"description":"Why write access to these paths is needed"}},"required":["paths","reason"],"additionalProperties":false}
        """;

    public async Task<ToolExecutionResult> Execute(ToolInvocation invocation, CancellationToken cancellationToken)
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

            if (securityProfile.ReadOnly)
            {
                throw new PermissionException("request_write_permission is not permitted by the current security profile");
            }

            var targets = paths.Select(SandboxWriteTarget.Resolve).Distinct().ToArray();
            var reply = await broker.Request(agentSession, reason, targets, cancellationToken).ConfigureAwait(false);
            if (reply.Kind == PermissionReplyKind.UserAway)
            {
                return "The user is away.";
            }

            if (reply.Decision == PermissionDecision.Grant)
            {
                return $"Runtime write permission recorded for this agent session; static policy and protected roots still apply: {string.Join(", ", targets.Select(target => target.Path))}";
            }

            return reply.Reason.Length == 0
                ? "Write permission request rejected."
                : $"Write permission request rejected: {reply.Reason}";
        }
        catch (Exception failure) when (failure is JsonException or FormatException or ArgumentException or IOException
                                        or UnauthorizedAccessException or PermissionException)
        {
            return $"error: {failure.Message}";
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
