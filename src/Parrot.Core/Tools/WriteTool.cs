using System.Text;
using System.Text.Json;
using Parrot.Permissions;
using Parrot.Security;

namespace Parrot.Tools;

internal sealed class WriteTool(
    ToolWorkspace workspace,
    SecurityProfile securityProfile,
    SandboxWriteGrants writeGrants) : ITool
{
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);

    public string Name => "write";

    public string Description =>
        "Create or replace a file with exact UTF-8 content. Relative paths resolve within the workspace; "
        + "absolute paths require explicit write authorization.";

    public string ParametersJson => WriteToolInput.Descriptor;

    public async Task<string> Execute(string argumentsJson, CancellationToken cancellationToken)
    {
        var writeGrantSnapshot = writeGrants.Capture();

        try
        {
            var input = JsonSerializer.Deserialize(
                argumentsJson,
                FileMutationJsonContext.Default.WriteToolInput)
                ?? throw new FormatException("Tool arguments must be an object.");
            var path = input.Path ?? throw new FormatException("Tool arguments require a string 'path'.");
            var content = input.Content ?? throw new FormatException("Tool arguments require a string 'content'.");
            if (path.Length == 0)
            {
                throw new FormatException("Tool argument 'path' must not be empty.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            var resolved = workspace.ResolveMutation(path, create: true, securityProfile, writeGrantSnapshot);
            FileMutation.RequireRegularFileOrMissing(resolved.Physical);
            var before = File.Exists(resolved.Physical)
                ? await File.ReadAllBytesAsync(resolved.Physical, cancellationToken).ConfigureAwait(false)
                : null;
            var after = Utf8WithoutBom.GetBytes(content);
            if (before is not null && before.AsSpan().SequenceEqual(after))
            {
                return FileMutation.NoChanges;
            }

            resolved = workspace.ResolveMutation(path, create: true, securityProfile, writeGrantSnapshot);
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
            return $"error: {failure.Message}";
        }
    }
}
