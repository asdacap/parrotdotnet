using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Parrot.Tools;

internal sealed class GitDiffTool(string workingDirectory) : ITool
{
    private const int MaxOutputCharacters = 4 << 20;
    private const int MaxErrorCharacters = 64 << 10;

    public string Name => "git_diff";

    public string Description =>
        "Read a bounded Git diff for uncommitted changes, a base branch, or a commit. "
        + "Uncommitted output also lists untracked paths.";

    public string ParametersJson =>
        """
        {"type":"object","properties":{"target":{"type":"string","enum":["uncommitted","base","commit"]},"ref":{"type":"string","description":"Required branch or commit when target is base or commit."}},"additionalProperties":false}
        """;

    public async Task<string> Execute(string argumentsJson, CancellationToken cancellationToken)
    {
        var arguments = Parse(argumentsJson);

        if (arguments.Error.Length > 0)
        {
            return $"error: {arguments.Error}";
        }

        try
        {
            var result = arguments.Target switch
            {
                "uncommitted" => await Uncommitted(cancellationToken).ConfigureAwait(false),
                "base" => await Base(arguments.Ref, cancellationToken).ConfigureAwait(false),
                "commit" => await RunGit(
                    ["show", "--format=fuller", "--no-ext-diff", "--no-textconv", "--find-renames",
                        arguments.Ref, "--"],
                    cancellationToken).ConfigureAwait(false),
                _ => throw new InvalidOperationException("Validated Git diff target was not recognized."),
            };

            if (result.Error.Length > 0)
            {
                return $"error: {result.Error}";
            }

            var output = string.IsNullOrWhiteSpace(result.Output) ? "No changes found." : result.Output;
            return result.Truncated
                ? output + "\n\n[git_diff output truncated; narrow the review target before drawing conclusions.]"
                : output;
        }
        catch (InvalidOperationException failure)
        {
            return $"error: {failure.Message}";
        }
    }

    private static Arguments Parse(string argumentsJson)
    {
        try
        {
            using var document = JsonDocument.Parse(argumentsJson);

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return Arguments.Invalid("git_diff arguments must be an object");
            }

            var target = ReadOptionalString(document.RootElement, "target");
            var reference = ReadOptionalString(document.RootElement, "ref").Trim();

            if (target.Length == 0)
            {
                target = "uncommitted";
            }

            if (target is not ("uncommitted" or "base" or "commit"))
            {
                return Arguments.Invalid("git_diff target must be uncommitted, base, or commit");
            }

            if (target == "uncommitted" && reference.Length > 0)
            {
                return Arguments.Invalid("git_diff ref is not valid for uncommitted changes");
            }

            if (target != "uncommitted" && !ValidRef(reference))
            {
                return Arguments.Invalid("git_diff requires a valid ref for base or commit");
            }

            return new Arguments(target, reference, string.Empty);
        }
        catch (JsonException)
        {
            return Arguments.Invalid("invalid JSON arguments");
        }
        catch (InvalidOperationException failure)
        {
            return Arguments.Invalid(failure.Message);
        }
    }

    private static string ReadOptionalString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
        {
            return string.Empty;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException($"git_diff {name} must be a string");
        }

        return value.GetString() ?? string.Empty;
    }

    private static bool ValidRef(string reference) =>
        reference.Length > 0
        && reference[0] != '-'
        && !reference.Any(character => char.IsControl(character) || char.IsWhiteSpace(character));

    private static void Append(StringBuilder output, string value)
    {
        if (value.Length == 0)
        {
            return;
        }

        if (output.Length > 0)
        {
            _ = output.AppendLine();
        }

        _ = output.Append(value);
    }

    private static async Task<BoundedOutput> ReadBounded(StreamReader reader, int limit)
    {
        var output = new StringBuilder(limit);
        var buffer = new char[4096];
        var truncated = false;

        while (true)
        {
            var read = await reader.ReadAsync(buffer).ConfigureAwait(false);

            if (read == 0)
            {
                break;
            }

            var remaining = limit - output.Length;

            if (remaining > 0)
            {
                _ = output.Append(buffer, 0, Math.Min(read, remaining));
            }

            truncated |= read > remaining;
        }

        return new BoundedOutput(output.ToString(), truncated);
    }

    private static void Kill(System.Diagnostics.Process process)
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
        }
    }

    private async Task<GitResult> Uncommitted(CancellationToken cancellationToken)
    {
        var staged = await RunGit(
            ["diff", "--cached", "--no-ext-diff", "--no-textconv", "--find-renames", "--"],
            cancellationToken).ConfigureAwait(false);

        if (staged.Error.Length > 0)
        {
            return staged;
        }

        var unstaged = await RunGit(
            ["diff", "--no-ext-diff", "--no-textconv", "--find-renames", "--"],
            cancellationToken).ConfigureAwait(false);

        if (unstaged.Error.Length > 0)
        {
            return unstaged;
        }

        var status = await RunGit(
            ["status", "--short", "--untracked-files=all"], cancellationToken).ConfigureAwait(false);

        if (status.Error.Length > 0)
        {
            return status;
        }

        var output = new StringBuilder();
        Append(output, staged.Output);
        Append(output, unstaged.Output);

        if (status.Output.Length > 0)
        {
            if (output.Length > 0)
            {
                _ = output.AppendLine().AppendLine();
            }

            _ = output.AppendLine("Git status (including untracked paths):").Append(status.Output);
        }

        return new GitResult(
            output.ToString(),
            string.Empty,
            staged.Truncated || unstaged.Truncated || status.Truncated);
    }

    private async Task<GitResult> Base(string reference, CancellationToken cancellationToken)
    {
        var mergeBase = await RunGit(
            ["merge-base", "HEAD", reference], cancellationToken).ConfigureAwait(false);

        if (mergeBase.Error.Length > 0)
        {
            return mergeBase;
        }

        var result = await RunGit(
            ["diff", "--no-ext-diff", "--no-textconv", "--find-renames", mergeBase.Output.Trim(), "--"],
            cancellationToken).ConfigureAwait(false);
        return result with { Truncated = result.Truncated || mergeBase.Truncated };
    }

    private async Task<GitResult> RunGit(
        IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        using var process = new System.Diagnostics.Process
        {
            StartInfo = StartInfo(arguments),
        };

        _ = process.Start();
        var stdout = ReadBounded(process.StandardOutput, MaxOutputCharacters);
        var stderr = ReadBounded(process.StandardError, MaxErrorCharacters);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            _ = await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            throw;
        }

        var output = await stdout.ConfigureAwait(false);
        var error = await stderr.ConfigureAwait(false);

        if (process.ExitCode == 0)
        {
            return new GitResult(output.Text, string.Empty, output.Truncated);
        }

        var message = error.Text.Trim();

        if (message.Length == 0)
        {
            message = $"git {arguments[0]} exited with code {process.ExitCode}";
        }
        else if (error.Truncated)
        {
            message += " [stderr truncated]";
        }

        return new GitResult(string.Empty, $"git_diff: git {arguments[0]}: {message}", false);
    }

    private ProcessStartInfo StartInfo(IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var argument in new[]
                 {
                     "--no-pager", "--no-optional-locks", "-c", "core.fsmonitor=false", "-c", "diff.external=",
                 })
        {
            start.ArgumentList.Add(argument);
        }

        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        return start;
    }

    private sealed record Arguments(string Target, string Ref, string Error)
    {
        public static Arguments Invalid(string error) => new(string.Empty, string.Empty, error);
    }

    private sealed record BoundedOutput(string Text, bool Truncated);

    private sealed record GitResult(string Output, string Error, bool Truncated);
}
