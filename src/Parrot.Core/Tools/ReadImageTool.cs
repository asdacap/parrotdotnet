using System.Text.Json;
using System.Text.Json.Serialization;
using Parrot.Agent;
using Parrot.Store;
using Parrot.Tools.Schema;

namespace Parrot.Tools;

internal sealed partial class ReadImageTool(ToolWorkspace workspace, ImageArtifactRepository artifacts) : ITool
{
    public string Name => "read_image";

    public string ParametersJson => Input.Descriptor;

    public async Task<ToolExecutionResult> Execute(
        ToolInvocation invocation,
        AgentTurnSelection selection,
        CancellationToken cancellationToken)
    {
        string path;

        try
        {
            var input = JsonSerializer.Deserialize(invocation.ArgumentsJson, FileToolJsonContext.Default.ReadImageToolInput)
                ?? throw new FormatException("Tool arguments must be an object.");
            path = input.Path ?? throw new FormatException("Tool arguments require a string 'path'.");
        }
        catch (Exception failure) when (failure is JsonException or FormatException)
        {
            return $"error: {failure.Message}";
        }

        (string Lexical, string Physical) resolved;
        try
        {
            resolved = workspace.ResolveRead(path);
        }
        catch (Exception failure) when (failure is InvalidOperationException or IOException)
        {
            return $"error: {failure.Message}";
        }

        if (!ToolWorkspace.AllowsRead(resolved, selection.SecurityProfile))
        {
            return "error: access denied";
        }

        if (!File.Exists(resolved.Physical))
        {
            return "error: no such file or directory";
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
            return new ToolExecutionResult("image read", null, [artifact]);
        }
        catch (Exception failure) when (failure is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return $"error: {failure.Message}";
        }
    }

    [ToolInputModel(AdditionalPropertiesPolicy.Closed)]
    internal sealed partial class Input
    {
        [JsonPropertyName("path")]
        [ToolRequired]
        public string? Path { get; init; }
    }
}
