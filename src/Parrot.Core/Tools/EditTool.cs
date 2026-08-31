using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Parrot.Agent;

namespace Parrot.Tools;

internal sealed class EditTool(
    ToolWorkspace workspace) : ITool
{
    private static readonly byte[] Utf8Preamble = [0xef, 0xbb, 0xbf];
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);

    public string Name => "edit";

    public async Task<ToolExecutionResult> Execute(
        ToolInvocation invocation,
        AgentTurnSelection selection,
        CancellationToken cancellationToken)
    {
        try
        {
            var input = JsonSerializer.Deserialize(
                invocation.ArgumentsJson,
                FileMutationJsonContext.Default.EditToolInput)
                ?? throw new FormatException("Tool arguments must be an object.");
            var path = input.Path ?? throw new FormatException("Tool arguments require a string 'path'.");
            var oldString = input.OldString
                ?? throw new FormatException("Tool arguments require a string 'old_string'.");
            var newString = input.NewString
                ?? throw new FormatException("Tool arguments require a string 'new_string'.");
            var replaceAll = input.ReplaceAll;
            if (path.Length == 0)
            {
                throw new FormatException("Tool argument 'path' must not be empty.");
            }

            if (oldString.Length == 0)
            {
                throw new FormatException("Tool argument 'old_string' must not be empty.");
            }

            if (oldString.Contains('\0', StringComparison.Ordinal)
                || newString.Contains('\0', StringComparison.Ordinal))
            {
                throw new FormatException("Edit arguments must not contain NUL.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            var resolved = workspace.ResolveMutation(path, create: false, selection.SecurityProfile);
            FileMutation.RequireRegularFile(resolved.Physical);
            var before = await File.ReadAllBytesAsync(resolved.Physical, cancellationToken).ConfigureAwait(false);
            var hasPreamble = before.AsSpan().StartsWith(Utf8Preamble);
            var textBytes = hasPreamble ? before.AsSpan(Utf8Preamble.Length) : before.AsSpan();
            if (textBytes.Contains((byte)0))
            {
                throw new FormatException("Cannot edit a binary file.");
            }

            var text = StrictUtf8.GetString(textBytes);
            var matches = CountMatches(text, oldString);
            if (!replaceAll && matches != 1)
            {
                throw new FormatException($"Expected exactly one old_string match, but found {matches}.");
            }

            if (matches == 0)
            {
                return FileMutation.NoChanges;
            }

            var replaced = replaceAll
                ? text.Replace(oldString, newString, StringComparison.Ordinal)
                : ReplaceOnce(text, oldString, newString);
            var replacementBytes = Utf8WithoutBom.GetBytes(replaced);
            var after = hasPreamble ? [.. Utf8Preamble, .. replacementBytes] : replacementBytes;
            if (before.AsSpan().SequenceEqual(after))
            {
                return FileMutation.NoChanges;
            }

            resolved = workspace.ResolveMutation(path, create: false, selection.SecurityProfile);
            FileMutation.RequireRegularFile(resolved.Physical);
            await FileMutation.Write(
                resolved.Physical,
                after,
                createParents: false,
                cancellationToken).ConfigureAwait(false);
            return FileDiff.Render([new FileDiff.FileChange(resolved.Display, before, after)]);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception failure) when (failure is JsonException
            or FormatException
            or DecoderFallbackException
            or IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or ArgumentException)
        {
            return $"error: {failure.Message}";
        }
    }

    private static int CountMatches(string text, string value)
    {
        var count = 0;
        for (var offset = 0; offset <= text.Length - value.Length;)
        {
            var match = text.IndexOf(value, offset, StringComparison.Ordinal);
            if (match < 0)
            {
                break;
            }

            count++;
            offset = match + value.Length;
        }

        return count;
    }

    private static string ReplaceOnce(string text, string oldString, string newString)
    {
        var index = text.IndexOf(oldString, StringComparison.Ordinal);
        return string.Concat(text.AsSpan(0, index), newString, text.AsSpan(index + oldString.Length));
    }

    internal sealed class Input
    {
        [JsonPropertyName("path")]
        public string? Path { get; init; }

        [JsonPropertyName("old_string")]
        public string? OldString { get; init; }

        [JsonPropertyName("new_string")]
        public string? NewString { get; init; }

        [JsonPropertyName("replace_all")]
        public bool ReplaceAll { get; init; }
    }
}
