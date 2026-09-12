using System.Text.Json;
using System.Text.Json.Serialization;
using Parrot.Agent;
using Parrot.Store;

namespace Parrot.Tools;

internal sealed class ReadImageTool(ToolWorkspace workspace, IImageArtifactRepository artifacts) : ITool
{
    public string Name => "read_image";

    public async Task<ToolExecutionResult> Execute(
        ToolInvocation invocation,
        AgentTurnSelection selection,
        CancellationToken cancellationToken)
    {
        if (invocation.ImageBudget is { IsExceeded: true } exhaustedBudget)
        {
            return new ToolExecutionResult(exhaustedBudget.DescribeFailure())
            {
                Outcome = ToolExecutionOutcome.ImageBudgetExceeded,
            };
        }

        string path;

        try
        {
            var input = JsonSerializer.Deserialize(invocation.ArgumentsJson, FileToolJsonContext.Default.ReadImageToolInput)
                ?? throw new FormatException("Tool arguments must be an object.");
            path = input.Path ?? throw new FormatException("Tool arguments require a string 'path'.");
        }
        catch (Exception failure) when (failure is JsonException or FormatException)
        {
            return ToolResultFormatter.Error(invocation, failure.Message);
        }

        (string Lexical, string Physical) resolved;
        try
        {
            resolved = workspace.ResolveRead(path);
        }
        catch (Exception failure) when (failure is InvalidOperationException or IOException)
        {
            return ToolResultFormatter.Error(invocation, failure.Message);
        }

        if (!ToolWorkspace.AllowsRead(resolved, selection.SecurityProfile))
        {
            return ToolResultFormatter.Error(invocation, "access denied");
        }

        if (!File.Exists(resolved.Physical))
        {
            return ToolResultFormatter.Error(invocation, "no such file or directory");
        }

        try
        {
            await using var source = File.OpenRead(resolved.Physical);
            var artifact = await artifacts.Persist(
                source,
                invocation.CallId,
                Path.GetFileName(resolved.Physical),
                "read_image",
                cancellationToken).ConfigureAwait(false);
            if (invocation.ImageBudget is { } budget && !budget.TryAccept(artifact.ByteLength))
            {
                return new ToolExecutionResult(budget.DescribeFailure())
                {
                    Outcome = ToolExecutionOutcome.ImageBudgetExceeded,
                };
            }

            return new ToolExecutionResult("image read", null, [artifact]);
        }
        catch (Exception failure) when (failure is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return ToolResultFormatter.Error(invocation, failure.Message);
        }
    }

    internal sealed class Input
    {
        [JsonPropertyName("path")]
        public string? Path { get; init; }
    }
}
