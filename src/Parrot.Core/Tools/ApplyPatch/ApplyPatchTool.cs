using System.Text;
using System.Text.Json;
using Parrot.Security;

namespace Parrot.Tools.ApplyPatch;

internal sealed class ApplyPatchTool(ToolWorkspace workspace, SecurityProfile security) : ITool
{
    public ApplyPatchTool(string workingDirectory, SecurityProfile security)
        : this(new ToolWorkspace(workingDirectory), security)
    {
    }

    public string Name => "apply_patch";

    public string Description =>
        "Apply reviewed edits written as aider SEARCH/REPLACE blocks or git-style unified diffs. Every nonempty aider SEARCH replaces all non-overlapping matches and reports its match count; unified hunks require unique context. Aider paths may be repeated for ordered edits; an empty SEARCH creates a missing file or fills an existing empty regular file. Paths are workspace-relative or explicitly security-authorized absolute paths. Existing line endings and UTF-8 BOMs are preserved.";

    public string ParametersJson =>
        """
        {"type":"object","properties":{"patchText":{"type":"string","description":"The patch text. Nonempty aider SEARCH blocks replace every non-overlapping match; unified update hunks require unique context."},"format":{"type":"string","enum":["aider","unified"],"description":"Patch syntax; aider is the default."}},"required":["patchText"],"additionalProperties":false}
        """;

    public Task<string> Execute(string argumentsJson, CancellationToken cancellationToken) =>
        Execution.Execute(workspace, security, argumentsJson, cancellationToken);

    private static class Execution
    {
        public static async Task<string> Execute(
            ToolWorkspace workspace,
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

            PatchApplicationPlan plan;
            try
            {
                plan = await Plan(workspace, security, patch, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (PatchException failure)
            {
                return $"error: {failure.Message}";
            }

            var written = new List<string>();
            try
            {
                foreach (var mutation in plan.Mutations)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var path = workspace.ResolveMutation(
                        mutation.Operation.Path,
                        mutation.Operation.Kind == PatchOperationKind.Add,
                        security);
                    await Commit(path.Physical, mutation, cancellationToken).ConfigureAwait(false);
                    written.Add(mutation.Operation.Path);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception failure)
            {
                var report = $"error: {failure.Message}";
                return written.Count == 0 ? report : $"{report}\nFiles written before failure: {string.Join(", ", written)}";
            }

            var diff = FileDiff.Render([.. plan.Mutations.Select(mutation => mutation.Change)]);
            var reports = plan.Mutations
                .SelectMany(mutation => mutation.MatchReports)
                .OrderBy(report => report.Order)
                .ToArray();
            if (reports.Length == 0)
            {
                return diff;
            }

            var summary = string.Join(
                '\n',
                reports.Select((report, index) =>
                    $"Chunk {index + 1} has {report.Count} {(report.Count == 1 ? "match" : "matches")}."));
            return $"{summary}\n\n{diff}";
        }

        private static async Task<PatchApplicationPlan> Plan(
            ToolWorkspace workspace,
            SecurityProfile security,
            Patch patch,
            CancellationToken cancellationToken)
        {
            List<string> errors = [];
            List<PatchMutation> mutations = [];

            foreach (var operation in patch.Operations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var resolved = workspace.ResolveMutation(
                        operation.Path,
                        operation.Kind == PatchOperationKind.Add,
                        security);
                    var mutation = await PlanOperation(operation, resolved, cancellationToken).ConfigureAwait(false);
                    mutations.Add(mutation);
                }
                catch (Exception failure) when (failure is PatchException or IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    errors.Add($"{operation.Kind.ToString().ToLowerInvariant()} '{operation.Path}': {failure.Message}");
                }
            }

            if (errors.Count > 0)
            {
                throw new PatchException($"patch planning failed with {errors.Count} errors:\n" + string.Join("\n", errors.Select((error, index) => $"{index + 1}. {error}")));
            }

            return new PatchApplicationPlan(mutations);
        }

        private static async Task<PatchMutation> PlanOperation(
            PatchOperation operation,
            ToolMutationPath resolved,
            CancellationToken cancellationToken)
        {
            switch (operation.Kind)
            {
                case PatchOperationKind.Add:
                {
                    EnsureRegularFileOrMissing(resolved.Physical);
                    var exists = File.Exists(resolved.Physical);
                    var before = exists
                        ? await File.ReadAllBytesAsync(resolved.Physical, cancellationToken).ConfigureAwait(false)
                        : null;
                    if (before is not null && before.Length != 0)
                    {
                        throw new PatchException("Empty SEARCH may only create a missing file or replace an empty file.");
                    }

                    return new PatchMutation(operation, resolved, before, Encoding.UTF8.GetBytes(operation.Data), []);
                }

                case PatchOperationKind.Update:
                {
                    EnsureRegularFile(resolved.Physical);
                    var before = await File.ReadAllBytesAsync(resolved.Physical, cancellationToken).ConfigureAwait(false);
                    var application = PatchApplicator.Apply(before, operation.Hunks);
                    return new PatchMutation(
                        operation,
                        resolved,
                        before,
                        application.Content,
                        application.MatchReports);
                }

                case PatchOperationKind.Delete:
                    EnsureRegularFile(resolved.Physical);
                    return new PatchMutation(
                        operation,
                        resolved,
                        await File.ReadAllBytesAsync(resolved.Physical, cancellationToken).ConfigureAwait(false),
                        null,
                        []);

                default:
                    throw new PatchException($"Unknown operation for '{operation.Path}'.");
            }
        }

        private static async Task Commit(string path, PatchMutation mutation, CancellationToken cancellationToken)
        {
            if (mutation.After is null)
            {
                EnsureRegularFile(path);
                File.Delete(path);
                return;
            }

            var existed = File.Exists(path);
            EnsureRegularFileOrMissing(path);
            var parent = Path.GetDirectoryName(path) ?? throw new PatchException($"Destination '{mutation.Operation.Path}' has no parent directory.");
            _ = Directory.CreateDirectory(parent);
            EnsureRegularFileOrMissing(path);
            await File.WriteAllBytesAsync(path, mutation.After, cancellationToken).ConfigureAwait(false);
            if (!existed && OperatingSystem.IsLinux())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
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
    }
}
