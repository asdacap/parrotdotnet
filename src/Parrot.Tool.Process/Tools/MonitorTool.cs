using System.Text.Json;
using System.Text.Json.Serialization;
using Parrot.Agent;
using Parrot.Config;
using Parrot.Diagnostics;
using Parrot.Process;
using Parrot.Store;

namespace Parrot.Tools;

internal sealed class MonitorTool(
    IProcessOwner processes,
    IAgentSession session,
    IPromptTemplateCatalog templates,
    ToolOutputBlobStore outputBlobs,
    IDiagnosticLog diagnostics) : ITool
{
    private const long DefaultTimeoutMilliseconds = 300_000;
    private const long MinimumTimeoutMilliseconds = 1_000;
    private const long MaximumTimeoutMilliseconds = 1_800_000;

    public string Name => "monitor";

    public Task<ToolExecutionResult> Execute(
        ToolInvocation invocation,
        AgentTurnSelection selection,
        CancellationToken cancellationToken) =>
        Task.FromResult(Start(invocation, selection));

    private ToolExecutionResult Start(ToolInvocation invocation, AgentTurnSelection selection)
    {
        try
        {
            ToolInputConversion.RequireObject(invocation.ArgumentsJson, "command");
            var input = ToolInputConversion.Deserialize(invocation.ArgumentsJson, MonitorToolJsonContext.Default.MonitorToolInput);
            var command = input.Command ?? throw new FormatException("Tool arguments require a string 'command'.");
            var name = input.Name?.Trim();
            var timeoutMilliseconds = input.TimeoutMilliseconds ?? DefaultTimeoutMilliseconds;

            if (command.Length == 0)
            {
                return ToolResultFormatter.Error(invocation, "no command given");
            }

            if (string.IsNullOrWhiteSpace(input.Description))
            {
                return ToolResultFormatter.Error(invocation, "description must not be empty");
            }

            if (name is { Length: 0 })
            {
                return ToolResultFormatter.Error(invocation, "process name must not be empty");
            }

            if (timeoutMilliseconds is < MinimumTimeoutMilliseconds or > MaximumTimeoutMilliseconds)
            {
                return ToolResultFormatter.Error(
                    invocation,
                    $"Tool argument 'timeout_ms' must be between {MinimumTimeoutMilliseconds} and {MaximumTimeoutMilliseconds}.");
            }

            var process = processes.Start(
                name,
                command,
                input.Description,
                invocation.CallId,
                input.Environment is null ? ProcessEnvironmentOverrides.Empty : new ProcessEnvironmentOverrides(input.Environment),
                session,
                selection.SecurityProfile,
                ShellProcessTerminalMode.Pipe,
                new ShellProcessMonitor(session, templates, outputBlobs, diagnostics, TimeSpan.FromMilliseconds(timeoutMilliseconds)));
            var yielded = process.Yield();
            return new ToolExecutionResult(
                templates.Render("monitor.started", [
                    new PromptTemplateArgument("name", process.Name),
                    new PromptTemplateArgument("stderr_path", yielded.StderrPath ?? string.Empty),
                ]),
                yielded);
        }
        catch (Exception failure) when (
            failure is JsonException or FormatException or SandboxUnavailableException or InvalidOperationException or IOException or PlatformNotSupportedException)
        {
            // Deliberate containment, as in exec_command: fail closed rather than
            // run the monitor outside the sandbox.
            return ToolResultFormatter.Error(invocation, failure.Message);
        }
    }

    internal sealed class Input
    {
        [JsonPropertyName("command")]
        public string? Command { get; init; }

        [JsonPropertyName("description")]
        public string? Description { get; init; }

        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("env")]
        [JsonConverter(typeof(ProcessEnvironmentJsonConverter))]
        public Dictionary<string, string>? Environment { get; init; }

        [JsonPropertyName("timeout_ms")]
        public long? TimeoutMilliseconds { get; init; }
    }
}
