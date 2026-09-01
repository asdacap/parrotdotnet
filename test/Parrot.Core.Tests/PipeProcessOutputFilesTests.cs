using Parrot.Process;

namespace Parrot.Core.Tests;

internal sealed class PipeProcessOutputFilesTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "parrot-live-pipe-output-tests", Guid.NewGuid().ToString("n"));

    public PipeProcessOutputFilesTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Test]
    public async Task Eagerly_creates_separate_concurrently_readable_files_and_preserves_unicode(
        CancellationToken cancellationToken)
    {
        await using var files = PipeProcessOutputFiles.Open(_directory);

        _ = await Assert.That(File.Exists(files.StdoutPath)).IsTrue();
        _ = await Assert.That(File.Exists(files.StderrPath)).IsTrue();
        _ = await Assert.That(files.StdoutPath).IsNotEqualTo(files.StderrPath);
        _ = await Assert.That(Path.GetDirectoryName(files.StdoutPath)).IsEqualTo(Path.GetFullPath(_directory));
        _ = await Assert.That(await File.ReadAllTextAsync(files.StdoutPath, cancellationToken)).IsEmpty();

        await files.AppendStdout("before €".AsMemory());
        await files.AppendStderr("failure".AsMemory());

        await using var stdout = new FileStream(files.StdoutPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stdout);
        _ = await Assert.That(await reader.ReadToEndAsync(cancellationToken)).IsEqualTo("before €");
        _ = await Assert.That(await File.ReadAllTextAsync(files.StderrPath, cancellationToken)).IsEqualTo("failure");

        if (!OperatingSystem.IsWindows())
        {
            _ = await Assert.That(File.GetUnixFileMode(files.StdoutPath)).IsEqualTo(
                UnixFileMode.UserRead | UnixFileMode.UserWrite);
            _ = await Assert.That(File.GetUnixFileMode(files.StderrPath)).IsEqualTo(
                UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Test]
    public async Task Retains_completed_files_and_deletes_only_startup_artifacts(CancellationToken cancellationToken)
    {
        string stdoutPath;
        string stderrPath;

        await using (var files = PipeProcessOutputFiles.Open(_directory))
        {
            stdoutPath = files.StdoutPath;
            stderrPath = files.StderrPath;
            await files.AppendStdout("complete".AsMemory());
            await files.CompleteStdout();
            await files.CompleteStderr();
        }

        _ = await Assert.That(await File.ReadAllTextAsync(stdoutPath, cancellationToken)).IsEqualTo("complete");
        _ = await Assert.That(File.Exists(stderrPath)).IsTrue();

        await using var partial = PipeProcessOutputFiles.Open(_directory);
        var partialStdout = partial.StdoutPath;
        var partialStderr = partial.StderrPath;
        partial.DeleteStartupArtifacts();

        _ = await Assert.That(File.Exists(partialStdout)).IsFalse();
        _ = await Assert.That(File.Exists(partialStderr)).IsFalse();
    }

    [Test]
    public async Task Preserves_supplementary_character_split_across_reader_boundary(
        CancellationToken cancellationToken)
    {
        await using var files = PipeProcessOutputFiles.Open(_directory);
        var output = string.Concat(new string('a', 4095), "😀", "tail");

        await files.AppendStdout(output.AsMemory(0, 4096));
        await files.AppendStdout(output.AsMemory(4096));
        await files.CompleteStdout();

        _ = await Assert.That(await File.ReadAllTextAsync(files.StdoutPath, cancellationToken)).IsEqualTo(output);
    }

    [Test]
    public async Task Replaces_an_unmatched_trailing_high_surrogate_on_completion(
        CancellationToken cancellationToken)
    {
        await using var files = PipeProcessOutputFiles.Open(_directory);

        await files.AppendStdout("before\ud83d".AsMemory());
        await files.CompleteStdout();

        _ = await Assert.That(await File.ReadAllTextAsync(files.StdoutPath, cancellationToken))
            .IsEqualTo("before�");
    }
}
