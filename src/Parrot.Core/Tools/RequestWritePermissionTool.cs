using System.Text.Json;
using System.Text.Json.Serialization;
using Parrot.Agent;
using Parrot.Permissions;

namespace Parrot.Tools;

internal sealed class RequestWritePermissionTool(
    PermissionBroker broker,
    AgentSession agentSession,
    ToolWorkspace workspace) : ITool
{
    public RequestWritePermissionTool(PermissionBroker broker, AgentSession agentSession)
        : this(broker, agentSession, new ToolWorkspace(Directory.GetCurrentDirectory()))
    {
    }

    public string Name => "request_write_permission";

    public string Description =>
        "Request agent-session-scoped permission for write, edit, and sandboxed shell operations on exact existing files or directories.";

    public string ParametersJson =>
        """
        {"type":"object","properties":{"paths":{"type":"array","minItems":1,"items":{"type":"string","minLength":1},"description":"Exact absolute paths of existing files or directories to make writable for write, edit, and sandboxed shell operations"},"reason":{"type":"string","minLength":1,"description":"Why write access to these paths is needed"}},"required":["paths","reason"],"additionalProperties":false}
        """;

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

            var targets = paths.Select(SandboxWriteTarget.Resolve).Distinct().ToArray();
            var externalTargets = targets.Where(target => !workspace.IsScratchPath(target.Path)).ToArray();
            if (externalTargets.Length == 0)
            {
                return $"Write permission already allowed for this agent scratch directory: {string.Join(", ", targets.Select(target => target.Path))}";
            }

            if (selection.SecurityProfile.ReadOnly)
            {
                throw new PermissionException("request_write_permission is not permitted by the current security profile");
            }

            if (externalTargets.All(target => selection.SecurityProfile.AllowsWrite(target.Path)))
            {
                return $"Write permission already allowed by the current security profile; static policy and protected roots still apply: {string.Join(", ", targets.Select(target => target.Path))}";
            }

            var reply = await broker.Request(agentSession, reason, externalTargets, cancellationToken).ConfigureAwait(false);
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
