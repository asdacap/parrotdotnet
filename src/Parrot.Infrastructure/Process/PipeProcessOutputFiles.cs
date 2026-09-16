namespace Parrot.Process;

internal sealed class PipeProcessOutputFiles : IAsyncDisposable
{
    private const int MaximumNameAttempts = 16;
    private readonly StreamOutputFile _stderr;
    private readonly StreamOutputFile _stdout;

    private PipeProcessOutputFiles(
        string stdoutPath,
        StreamOutputFile stdout,
        string stderrPath,
        StreamOutputFile stderr)
    {
        StdoutPath = stdoutPath;
        _stdout = stdout;
        StderrPath = stderrPath;
        _stderr = stderr;
    }

    public string StdoutPath { get; }

    public string StderrPath { get; }

    public static PipeProcessOutputFiles Open(string directory) =>
        Open(directory, new HaikunatorNameGenerator().Next);

    public Task AppendStdout(ReadOnlyMemory<char> output) => _stdout.Append(output);

    public Task AppendStderr(ReadOnlyMemory<char> output) => _stderr.Append(output);

    public ValueTask CompleteStdout() => _stdout.Complete();

    public ValueTask CompleteStderr() => _stderr.Complete();

    public async ValueTask DisposeAsync()
    {
        await _stdout.DisposeAsync().ConfigureAwait(false);
        await _stderr.DisposeAsync().ConfigureAwait(false);
    }

    public void DeleteStartupArtifacts()
    {
        _stdout.Dispose();
        _stderr.Dispose();
        File.Delete(StdoutPath);
        File.Delete(StderrPath);
    }

    internal static PipeProcessOutputFiles Open(string directory, Func<string> nextName)
    {
        ArgumentNullException.ThrowIfNull(nextName);
        ProcessOutputBlobStore.EnsureDirectory(directory);
        var fullDirectory = Path.GetFullPath(directory);

        for (var attempt = 0; attempt < MaximumNameAttempts; attempt++)
        {
            var name = nextName();
            if (!string.Equals(Path.GetFileName(name), name, StringComparison.Ordinal)
                || !name.EndsWith("-arse.dat", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The pipe output file name must be a safe -arse.dat basename.");
            }

            var stem = name[..^"-arse.dat".Length];
            var stdoutPath = Path.Combine(fullDirectory, $"{stem}-stdout-arse.dat");
            var stderrPath = Path.Combine(fullDirectory, $"{stem}-stderr-arse.dat");
            StreamOutputFile? stdout = null;
            StreamOutputFile? stderr = null;
            var stdoutCreated = false;
            var stderrCreated = false;

            try
            {
                stdout = StreamOutputFile.CreateNew(stdoutPath);
                stdoutCreated = true;
                stderr = StreamOutputFile.CreateNew(stderrPath);
                stderrCreated = true;
                return new PipeProcessOutputFiles(stdoutPath, stdout, stderrPath, stderr);
            }
            catch (IOException) when (!stdoutCreated && File.Exists(stdoutPath))
            {
            }
            catch (IOException) when (!stderrCreated && File.Exists(stderrPath))
            {
                stdout?.Dispose();
                File.Delete(stdoutPath);
            }
            catch
            {
                stderr?.Dispose();
                stdout?.Dispose();

                if (stderrCreated)
                {
                    File.Delete(stderrPath);
                }

                if (stdoutCreated)
                {
                    File.Delete(stdoutPath);
                }

                throw;
            }
        }

        throw new IOException($"Could not create unique live pipe output files in '{fullDirectory}'.");
    }
}
