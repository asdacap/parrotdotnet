namespace Parrot.Tools;

internal sealed class Patch(IReadOnlyList<PatchOperation> operations)
{
    public IReadOnlyList<PatchOperation> Operations { get; } = operations;

    public static Patch Parse(string text, PatchFormat format) =>
        format switch
        {
            PatchFormat.Aider => AiderPatchParser.Parse(text),
            PatchFormat.Unified => UnifiedPatchParser.Parse(text),
            _ => throw new PatchException($"Unknown patch format '{format}'."),
        };

    internal static Patch Validate(IReadOnlyList<PatchOperation> operations)
    {
        if (operations.Count == 0)
        {
            throw new PatchException("Patch has no file operations.");
        }

        foreach (var operation in operations)
        {
            ValidatePath(operation.Path);

            if (operation.Kind == PatchOperationKind.Add && operation.Data.Length == 0)
            {
                throw new PatchException($"File creation for '{operation.Path}' has no content.");
            }
        }

        var paths = operations.Select(operation => operation.Path).Order(StringComparer.Ordinal).ToArray();

        for (var index = 1; index < paths.Length; index++)
        {
            var previous = paths[index - 1];
            var current = paths[index];

            if (string.Equals(previous, current, StringComparison.Ordinal))
            {
                throw new PatchException($"Duplicate or cycling path '{current}'.");
            }

            if (current.StartsWith(previous + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                throw new PatchException($"Overlapping paths '{previous}' and '{current}'.");
            }
        }

        return new Patch(operations);
    }

    internal static void ValidatePath(string path)
    {
        if (path.Length == 0 || path.Contains('\0', StringComparison.Ordinal))
        {
            throw new PatchException("Path must be a clean workspace path.");
        }

        string cleaned;

        try
        {
            cleaned = Path.IsPathFullyQualified(path)
                ? Path.GetFullPath(path)
                : Path.GetRelativePath(
                    Path.DirectorySeparatorChar.ToString(),
                    Path.GetFullPath(path, Path.DirectorySeparatorChar.ToString()));
        }
        catch (Exception failure) when (failure is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new PatchException("Path must be a clean workspace path.", failure);
        }

        if (!string.Equals(path, cleaned, StringComparison.Ordinal)
            || path is "." or ".."
            || path.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new PatchException("Path must be a clean workspace path.");
        }
    }
}
