using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Parrot.Agent;

namespace Parrot.Tools;

internal sealed class WriteTool(
    ToolWorkspace workspace) : ITool
{
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);

    public string Name => "write";

    public async Task<ToolExecutionResult> Execute(
        ToolInvocation invocation,
        AgentTurnSelection selection,
        CancellationToken cancellationToken)
    {
        try
        {
            var input = JsonSerializer.Deserialize(
                invocation.ArgumentsJson,
                FileMutationJsonContext.Default.WriteToolInput)
                ?? throw new FormatException("Tool arguments must be an object.");
            var path = input.Path ?? throw new FormatException("Tool arguments require a string 'path'.");
            var content = input.Content ?? throw new FormatException("Tool arguments require a string 'content'.");
            if (path.Length == 0)
            {
                throw new FormatException("Tool argument 'path' must not be empty.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            var resolved = workspace.ResolveMutation(path, create: true, selection.SecurityProfile);
            FileMutation.RequireRegularFileOrMissing(resolved.Physical);
            var before = File.Exists(resolved.Physical)
                ? await File.ReadAllBytesAsync(resolved.Physical, cancellationToken).ConfigureAwait(false)
                : null;
            var after = Utf8WithoutBom.GetBytes(content);
            if (before is not null && before.AsSpan().SequenceEqual(after))
            {
                return FileMutation.NoChanges;
            }

            resolved = workspace.ResolveMutation(path, create: true, selection.SecurityProfile);
            await FileMutation.Write(
                resolved.Physical,
                after,
                createParents: true,
                cancellationToken).ConfigureAwait(false);
            return FileDiff.Render([new FileDiff.FileChange(resolved.Display, before, after)]);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception failure) when (failure is JsonException
            or FormatException
            or IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or ArgumentException)
        {
            return ToolResultFormatter.Error(invocation, failure.Message);
        }
    }

    internal sealed class Input
    {
        [JsonPropertyName("path")]
        public string? Path { get; init; }

        [JsonPropertyName("content")]
        public string? Content { get; init; }
    }
}
