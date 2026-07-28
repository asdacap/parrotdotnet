using System.Text;
using System.Text.Json;
using Parrot.Security;

namespace Parrot.Tools.ApplyPatch;

internal sealed class ApplyPatchTool(string workingDirectory, SecurityProfile security) : ITool
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public string Name => "apply_patch";

    public string Description =>
        "Apply reviewed workspace edits written as aider SEARCH/REPLACE blocks: a file path on its own line, then '<<<<<<< SEARCH', the exact existing lines, '=======', the replacement lines, and '>>>>>>> REPLACE'. An empty SEARCH section creates the file. Set format to \"unified\" to supply git-style unified diff text instead.";

    public string ParametersJson =>
        """
        {"type":"object","properties":{"patchText":{"type":"string","description":"The patch text, written in the format named by the format field."},"format":{"type":"string","enum":["aider","unified"],"description":"Edit syntax of patchText, defaulting to aider. \"aider\": one or more SEARCH/REPLACE blocks, each a workspace-relative file path on its own line, then <<<<<<< SEARCH, the exact lines to replace, =======, the replacement lines, and >>>>>>> REPLACE; repeat blocks under the same path for several edits to one file, and leave the SEARCH section empty to create a new file. \"unified\": git diff text with --- and +++ headers and @@ hunks; a /dev/null source creates the file and a /dev/null target deletes it, and renames are rejected."}},"required":["patchText"],"additionalProperties":false}
        """;

    public Task<string> Execute(string argumentsJson, CancellationToken cancellationToken) =>
        Execution.Execute(workingDirectory, security, argumentsJson, cancellationToken);

    private static class Execution
    {
        public static async Task<string> Execute(
            string workingDirectory,
            SecurityProfile security,
            string argumentsJson,
            CancellationToken cancellationToken)
        {
            Patch patch;

            try
            {
                var (text, format) = ReadArguments(argumentsJson);
                patch = Patch.Parse(text, format);
            }
            catch (Exception failure) when (failure is JsonException or PatchException)
            {
                return $"error: {failure.Message}";
            }

            var written = new List<string>();

            try
            {
                Preflight(workingDirectory, security, patch.Operations);

                foreach (var operation in patch.Operations)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await Apply(workingDirectory, operation, cancellationToken).ConfigureAwait(false);
                    written.Add(operation.Path);
                }
            }
            catch (Exception failure) when (failure is not OperationCanceledException)
            {
                var report = $"error: {failure.Message}";
                return written.Count == 0
                    ? report
                    : $"{report}\nFiles written before failure: {string.Join(", ", written)}";
            }

            return $"Applied patch to {string.Join(", ", written)}";
        }

        private static void Preflight(
            string workingDirectory,
            SecurityProfile security,
            IReadOnlyList<PatchOperation> operations)
        {
            foreach (var operation in operations)
            {
                var path = Resolve(
                    workingDirectory,
                    operation.Path,
                    operation.Kind == PatchOperationKind.Add);

                if (!security.AllowsWrite(path))
                {
                    throw new PatchException($"Write access denied for '{operation.Path}'.");
                }
            }
        }

        private static (string Text, PatchFormat Format) ReadArguments(string json)
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("patchText", out var textElement)
                || textElement.ValueKind != JsonValueKind.String)
            {
                throw new PatchException("apply_patch requires a string patchText.");
            }

            var format = PatchFormat.Aider;

            if (root.TryGetProperty("format", out var formatElement))
            {
                if (formatElement.ValueKind != JsonValueKind.String)
                {
                    throw new PatchException("apply_patch format must be 'aider' or 'unified'.");
                }

                format = formatElement.GetString() switch
                {
                    null or "aider" => PatchFormat.Aider,
                    "unified" => PatchFormat.Unified,
                    var value => throw new PatchException($"Unknown patch format '{value}'."),
                };
            }

            return (textElement.GetString() ?? string.Empty, format);
        }

        private static async Task Apply(
            string workingDirectory,
            PatchOperation operation,
            CancellationToken cancellationToken)
        {
            var path = Resolve(workingDirectory, operation.Path, operation.Kind == PatchOperationKind.Add);

            switch (operation.Kind)
            {
                case PatchOperationKind.Add:
                    EnsureRegularFileOrMissing(path);
                    var parent = Path.GetDirectoryName(path)
                        ?? throw new PatchException($"Destination '{operation.Path}' has no parent directory.");
                    _ = Directory.CreateDirectory(parent);
                    path = Resolve(workingDirectory, operation.Path, create: true);
                    EnsureRegularFileOrMissing(path);
                    await File.WriteAllBytesAsync(
                        path, StrictUtf8.GetBytes(operation.Data), cancellationToken).ConfigureAwait(false);
                    break;

                case PatchOperationKind.Update:
                    EnsureRegularFile(path);
                    var before = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
                    var after = ApplyHunks(before, operation.Hunks);
                    path = Resolve(workingDirectory, operation.Path, create: false);
                    EnsureRegularFile(path);
                    await File.WriteAllBytesAsync(path, after, cancellationToken).ConfigureAwait(false);
                    break;

                case PatchOperationKind.Delete:
                    EnsureRegularFile(path);
                    path = Resolve(workingDirectory, operation.Path, create: false);
                    EnsureRegularFile(path);
                    File.Delete(path);
                    break;

                default:
                    throw new PatchException($"Unknown operation for '{operation.Path}'.");
            }
        }

        private static byte[] ApplyHunks(byte[] data, IReadOnlyList<PatchHunk> hunks)
        {
            var bom = data.AsSpan().StartsWith(Encoding.UTF8.Preamble);
            var content = bom ? data[Encoding.UTF8.Preamble.Length..] : data;
            var text = StrictUtf8.GetString(content);
            var lineEnding = text.Contains("\r\n", StringComparison.Ordinal)
                && !text.Replace("\r\n", string.Empty, StringComparison.Ordinal).Contains('\n', StringComparison.Ordinal)
                    ? "\r\n"
                    : "\n";
            var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();

            if (lines.Count > 0 && lines[^1].Length == 0)
            {
                lines.RemoveAt(lines.Count - 1);
            }

            var replacements = new List<Replacement>();
            var lineIndex = 0;

            foreach (var hunk in hunks)
            {
                var oldLines = hunk.Lines.Where(line => line.Kind != '+').Select(line => line.Text).ToArray();
                var newLines = hunk.Lines.Where(line => line.Kind != '-').Select(line => line.Text).ToArray();

                if (oldLines.Length == 0)
                {
                    replacements.Add(new Replacement(lines.Count, 0, newLines));
                    continue;
                }

                var found = SeekSequence(lines, oldLines, lineIndex);
                replacements.Add(new Replacement(found, oldLines.Length, newLines));
                lineIndex = found + oldLines.Length;
            }

            foreach (var replacement in replacements.OrderByDescending(item => item.Start))
            {
                lines.RemoveRange(replacement.Start, replacement.OldCount);
                lines.InsertRange(replacement.Start, replacement.Lines);
            }

            var output = lines.Count == 0 ? string.Empty : string.Join(lineEnding, lines) + lineEnding;
            var encoded = StrictUtf8.GetBytes(output);

            if (!bom)
            {
                return encoded;
            }

            var result = new byte[Encoding.UTF8.Preamble.Length + encoded.Length];
            Encoding.UTF8.Preamble.CopyTo(result);
            encoded.CopyTo(result, Encoding.UTF8.Preamble.Length);
            return result;
        }

        private static int SeekSequence(List<string> lines, string[] pattern, int start)
        {
            Func<string, string, bool>[] comparisons =
            [
                static (left, right) => string.Equals(left, right, StringComparison.Ordinal),
            static (left, right) => string.Equals(
                left.TrimEnd(' ', '\t', '\r', '\n'),
                right.TrimEnd(' ', '\t', '\r', '\n'),
                StringComparison.Ordinal),
            static (left, right) => string.Equals(left.Trim(), right.Trim(), StringComparison.Ordinal),
            static (left, right) => string.Equals(
                Normalize(left.Trim()), Normalize(right.Trim()), StringComparison.Ordinal),
        ];

            foreach (var equal in comparisons)
            {
                var found = -1;
                var count = 0;

                for (var index = start; index <= lines.Count - pattern.Length; index++)
                {
                    if (!SequenceEqual(lines, pattern, index, equal))
                    {
                        continue;
                    }

                    found = index;
                    count++;
                }

                if (count > 1)
                {
                    throw new PatchException(
                        $"Found {count} matches for {DescribePattern(pattern)}; include more surrounding lines.");
                }

                if (count == 1)
                {
                    return found;
                }
            }

            throw new PatchException($"Failed to find expected lines {DescribePattern(pattern)}.");
        }

        private static bool SequenceEqual(
            List<string> lines,
            string[] pattern,
            int start,
            Func<string, string, bool> equal)
        {
            for (var index = 0; index < pattern.Length; index++)
            {
                if (!equal(lines[start + index], pattern[index]))
                {
                    return false;
                }
            }

            return true;
        }

        private static string DescribePattern(string[] pattern)
        {
            const int maxLength = 1024;
            var text = string.Join("\n", pattern);

            return text.Length <= maxLength
                ? $"'{text}'"
                : $"'{text[..maxLength]}' (... {text.Length - maxLength} characters omitted)";
        }

        private static string Normalize(string value) =>
            value
                .Replace('‘', '\'')
                .Replace('’', '\'')
                .Replace('‚', '\'')
                .Replace('‛', '\'')
                .Replace('“', '"')
                .Replace('”', '"')
                .Replace('„', '"')
                .Replace('‟', '"')
                .Replace('‐', '-')
                .Replace('‑', '-')
                .Replace('‒', '-')
                .Replace('–', '-')
                .Replace('—', '-')
                .Replace('―', '-')
                .Replace("…", "...", StringComparison.Ordinal)
                .Replace(' ', ' ');

        private static string Resolve(string workingDirectory, string requestedPath, bool create)
        {
            var root = CanonicalRoot(workingDirectory);
            var path = Path.GetFullPath(Path.Combine(root, requestedPath));

            if (!Contains(root, path))
            {
                throw new PatchException($"Path '{requestedPath}' escapes the working directory.");
            }

            var relative = Path.GetRelativePath(root, path);
            var current = root;
            var parts = relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            var lastExisting = create ? parts.Length - 1 : parts.Length;

            for (var index = 0; index < lastExisting; index++)
            {
                current = Path.Combine(current, parts[index]);

                if (!Path.Exists(current))
                {
                    if (create)
                    {
                        break;
                    }

                    throw new FileNotFoundException($"Source '{requestedPath}' is missing.");
                }

                var attributes = File.GetAttributes(current);

                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new PatchException($"Path '{requestedPath}' traverses a symbolic link.");
                }

                if (index < parts.Length - 1 && (attributes & FileAttributes.Directory) == 0)
                {
                    throw new PatchException($"Parent of '{requestedPath}' is not a directory.");
                }
            }

            return path;
        }

        private static string CanonicalRoot(string workingDirectory)
        {
            var root = new DirectoryInfo(Path.GetFullPath(workingDirectory));
            var target = root.ResolveLinkTarget(returnFinalTarget: true);
            return Path.TrimEndingDirectorySeparator((target ?? root).FullName);
        }

        private static bool Contains(string root, string path)
        {
            var relative = Path.GetRelativePath(root, path);
            return relative != ".."
                && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !Path.IsPathFullyQualified(relative);
        }

        private static void EnsureRegularFile(string path)
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"Source '{path}' is missing.");
            }

            EnsureRegularFileOrMissing(path);
        }

        private static void EnsureRegularFileOrMissing(string path)
        {
            if (!Path.Exists(path))
            {
                return;
            }

            var attributes = File.GetAttributes(path);

            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            {
                throw new PatchException("Patches require regular files.");
            }
        }

        private sealed record Replacement(int Start, int OldCount, string[] Lines);
    }
}
