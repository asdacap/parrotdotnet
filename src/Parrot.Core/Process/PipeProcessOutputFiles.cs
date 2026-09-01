using System.Text;

namespace Parrot.Process;

internal sealed class PipeProcessOutputFiles : IAsyncDisposable
{
    private const int MaximumNameAttempts = 16;
    private const UnixFileMode FilePermissions = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);
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
                stdout = StreamOutputFile.Open(stdoutPath);
                stdoutCreated = true;
                stderr = StreamOutputFile.Open(stderrPath);
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

    private static StreamWriter OpenWriter(string path)
    {
        var options = new FileStreamOptions
        {
            Access = FileAccess.Write,
            Mode = FileMode.CreateNew,
            Share = FileShare.ReadWrite,
            Options = FileOptions.Asynchronous,
        };

        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = FilePermissions;
        }

        return new StreamWriter(path, Utf8WithoutBom, options);
    }

    private sealed class StreamOutputFile(string path) : IDisposable, IAsyncDisposable
    {
        private readonly StreamWriter _writer = OpenWriter(path);
        private char? _trailingHighSurrogate;

        public static StreamOutputFile Open(string path) => new(path);

        public async Task Append(ReadOnlyMemory<char> output)
        {
            var start = 0;

            if (_trailingHighSurrogate is char trailing)
            {
                if (!output.IsEmpty && char.IsLowSurrogate(output.Span[0]))
                {
                    await _writer.WriteAsync(string.Concat(trailing, output.Span[0])).ConfigureAwait(false);
                    start = 1;
                }
                else
                {
                    await _writer.WriteAsync(trailing).ConfigureAwait(false);
                }

                _trailingHighSurrogate = null;
            }

            var length = output.Length - start;

            if (length > 0 && char.IsHighSurrogate(output.Span[^1]))
            {
                _trailingHighSurrogate = output.Span[^1];
                length--;
            }

            if (length > 0)
            {
                await _writer.WriteAsync(output.Slice(start, length)).ConfigureAwait(false);
            }

            await _writer.FlushAsync().ConfigureAwait(false);
        }

        public async ValueTask Complete()
        {
            if (_trailingHighSurrogate is char trailing)
            {
                await _writer.WriteAsync(trailing).ConfigureAwait(false);
                _trailingHighSurrogate = null;
            }

            await _writer.DisposeAsync().ConfigureAwait(false);
        }

        public void Dispose() => _writer.Dispose();

        public ValueTask DisposeAsync() => Complete();
    }
}
