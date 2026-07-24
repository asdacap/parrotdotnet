using System.Diagnostics;
using Parrot.Tools;

namespace Parrot.Core.Tests;

internal sealed class GitDiffToolTests : IDisposable
{
    private readonly string _repository = Path.Combine(
        Path.GetTempPath(), "parrot-git-diff-tests", Guid.NewGuid().ToString("n"));

    public GitDiffToolTests() => Directory.CreateDirectory(_repository);

    public void Dispose()
    {
        if (Directory.Exists(_repository))
        {
            Directory.Delete(_repository, recursive: true);
        }
    }

    [Test]
    public async Task Reads_supported_targets_and_rejects_option_refs(CancellationToken cancellationToken)
    {
        await RunGit(cancellationToken, "init");
        await RunGit(cancellationToken, "config", "user.email", "test@example.com");
        await RunGit(cancellationToken, "config", "user.name", "Test");
        await File.WriteAllTextAsync(
            Path.Combine(_repository, "tracked.txt"), "before\n", cancellationToken);
        await RunGit(cancellationToken, "add", "tracked.txt");
        await RunGit(cancellationToken, "commit", "-m", "initial");
        var tool = new GitDiffTool(_repository);
        var cleanBase = await tool.Execute("""{"target":"base","ref":"HEAD"}""", cancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(_repository, "tracked.txt"), "after\n", cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(_repository, "new.txt"), "new\n", cancellationToken);

        var uncommitted = await tool.Execute("{}", cancellationToken);
        var commit = await tool.Execute("""{"target":"commit","ref":"HEAD"}""", cancellationToken);
        var invalid = await tool.Execute(
            """{"target":"base","ref":"--help"}""", cancellationToken);

        _ = await Assert.That(uncommitted).Contains("-before");
        _ = await Assert.That(uncommitted).Contains("+after");
        _ = await Assert.That(uncommitted).Contains("?? new.txt");
        _ = await Assert.That(commit).Contains("initial");
        _ = await Assert.That(cleanBase).IsEqualTo("No changes found.");
        _ = await Assert.That(invalid).Contains("valid ref");
    }

    private async Task RunGit(CancellationToken cancellationToken, params string[] arguments)
    {
        using var process = new System.Diagnostics.Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = _repository,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        _ = process.Start();
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(await error);
        }
    }
}
