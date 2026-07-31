using System.Text;

namespace Parrot.Process;

internal sealed class ShellProcessExecution : IAsyncDisposable
{
    private const int MaxFormattedOutputBytes = 64 << 10;
    private const int MaxStreamOutputCharacters = 64 << 10;
    private const string BridgeReady = "READY";
    private readonly CancellationTokenSource _cancellation;
    private readonly string _blobDirectory;
    private readonly System.Diagnostics.Process _process;
    private readonly PtyTranscript? _transcript;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly int _masterDescriptor;
    private readonly int _slaveDescriptor;
    private readonly CancellationTokenRegistration _cancellationRegistration;
    private ProcessResult? _completedResult;
    private int _disposed;
    private int _masterClosed;
    private int _slaveClosed;

    internal ShellProcessExecution(
        System.Diagnostics.Process process,
        string blobDirectory,
        CancellationToken cancellationToken)
    {
        _process = process;
        _blobDirectory = blobDirectory;
        _masterDescriptor = -1;
        _slaveDescriptor = -1;
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _cancellationRegistration = _cancellation.Token.Register(() => Kill(_process));
        Result = RunPipe();
    }

    internal ShellProcessExecution(
        System.Diagnostics.Process process,
        int masterDescriptor,
        int slaveDescriptor,
        string blobDirectory,
        CancellationToken cancellationToken)
    {
        _process = process;
        _blobDirectory = blobDirectory;
        _masterDescriptor = masterDescriptor;
        _slaveDescriptor = slaveDescriptor;
        _transcript = new PtyTranscript(blobDirectory);
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _cancellationRegistration = _cancellation.Token.Register(() => Kill(_process));
        Result = RunPseudoTerminal(_transcript);
    }

    public Task<ProcessResult> Result { get; }

    public bool IsPseudoTerminal => _transcript is not null;

    public (long Cursor, string Text) ReadTranscript(long offset)
    {
        var transcript = _transcript;

        if (transcript is null)
        {
            ArgumentOutOfRangeException.ThrowIfNotEqual(offset, 0);
            return (0, string.Empty);
        }

        return transcript.Read(offset);
    }

    public async Task WriteStdin(string input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (!IsPseudoTerminal)
        {
            throw new InvalidOperationException("Standard input is available only for pseudo-terminal processes.");
        }

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (Result.IsCompleted || Volatile.Read(ref _masterClosed) != 0)
            {
                throw new InvalidOperationException("The pseudo-terminal process has completed.");
            }

            var bytes = Encoding.UTF8.GetBytes(input);
            var written = 0;

            while (written < bytes.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                written += LinuxPseudoTerminal.Write(_masterDescriptor, bytes.AsSpan(written), cancellationToken);
            }
        }
        catch (IOException) when (Result.IsCompleted || Volatile.Read(ref _masterClosed) != 0)
        {
            throw new InvalidOperationException("The pseudo-terminal process has completed.");
        }
        finally
        {
            _ = _writeGate.Release();
        }
    }

    public Task Cancel() => _cancellation.CancelAsync();

    public (long Cursor, ProcessResult Result) ReadResult(long offset)
    {
        var completed = Volatile.Read(ref _completedResult)
            ?? throw new InvalidOperationException("The process has not completed.");

        if (offset == 0)
        {
            return (_transcript?.Length ?? 0, completed);
        }

        var transcript = _transcript
            ?? throw new InvalidOperationException(
                "Incremental results are available only for pseudo-terminal processes.");
        var (cursor, output) = transcript.ReadOutput(offset);

        if (!output.Spilled)
        {
            var result = new ProcessResult(completed.ExitCode, output.Text, string.Empty, string.Empty);

            if (Encoding.UTF8.GetByteCount(ProcessResultFormatter.Format(result)) <= MaxFormattedOutputBytes)
            {
                return (cursor, result);
            }
        }

        var blobPath = new ProcessOutputBlobStore(_blobDirectory)
            .PersistImmediately(completed.ExitCode, output);
        return (cursor, new ProcessResult(completed.ExitCode, string.Empty, string.Empty, blobPath));
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _cancellation.CancelAsync().ConfigureAwait(false);

        try
        {
            await AwaitCompletionForDisposal().ConfigureAwait(false);
        }
        finally
        {
            await _cancellationRegistration.DisposeAsync().ConfigureAwait(false);
            _cancellation.Dispose();
            _writeGate.Dispose();
            _transcript?.Dispose();
            _process.Dispose();
        }
    }

    private static async Task<ProcessResult> FormatResult(
        int exitCode,
        ProcessOutput stdout,
        ProcessOutput stderr,
        string blobDirectory,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!stdout.Spilled && !stderr.Spilled)
            {
                var result = new ProcessResult(exitCode, stdout.Text, stderr.Text, string.Empty);

                if (Encoding.UTF8.GetByteCount(ProcessResultFormatter.Format(result)) <= MaxFormattedOutputBytes)
                {
                    return result;
                }
            }

            var blobPath = await new ProcessOutputBlobStore(blobDirectory)
                .Persist(exitCode, stdout, stderr, cancellationToken)
                .ConfigureAwait(false);
            return new ProcessResult(exitCode, string.Empty, string.Empty, blobPath);
        }
        finally
        {
            stdout.DeleteTemporaryFile();
            stderr.DeleteTemporaryFile();
        }
    }

    private static async Task<ProcessOutput> ReadBounded(StreamReader reader, string blobDirectory)
    {
        var output = new StringBuilder(MaxStreamOutputCharacters);
        var buffer = new char[4096];

        while (true)
        {
            var read = await reader.ReadAsync(buffer).ConfigureAwait(false);

            if (read == 0)
            {
                return new ProcessOutput(output.ToString(), string.Empty);
            }

            if (output.Length + read <= MaxStreamOutputCharacters)
            {
                _ = output.Append(buffer, 0, read);
                continue;
            }

            ProcessOutputBlobStore.EnsureDirectory(blobDirectory);
            var temporaryPath = Path.Combine(blobDirectory, $".process-{Guid.NewGuid():n}.tmp");

            try
            {
                await Spill(reader, output, buffer.AsMemory(0, read), temporaryPath).ConfigureAwait(false);
                return new ProcessOutput(string.Empty, temporaryPath);
            }
            catch
            {
                File.Delete(temporaryPath);
                throw;
            }
        }
    }

    private static async Task Spill(
        StreamReader reader,
        StringBuilder initial,
        ReadOnlyMemory<char> firstOverflow,
        string path)
    {
        var fileOptions = new FileStreamOptions
        {
            Access = FileAccess.Write,
            Mode = FileMode.CreateNew,
            Options = FileOptions.Asynchronous,
        };

        if (!OperatingSystem.IsWindows())
        {
            fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        await using var stream = new FileStream(path, fileOptions);
        await using var writer = new StreamWriter(stream);
        await writer.WriteAsync(initial.ToString()).ConfigureAwait(false);
        await writer.WriteAsync(firstOverflow).ConfigureAwait(false);
        var buffer = new char[4096];

        while (true)
        {
            var read = await reader.ReadAsync(buffer).ConfigureAwait(false);

            if (read == 0)
            {
                return;
            }

            await writer.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
        }
    }

    private static async Task DrainAndDelete(params Task<ProcessOutput>[] outputs)
    {
        foreach (var task in outputs)
        {
            try
            {
                var output = await task.ConfigureAwait(false);
                output.DeleteTemporaryFile();
            }
            catch
            {
            }
        }
    }

    private static void Kill(System.Diagnostics.Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    private async Task<ProcessResult> RunPipe()
    {
        var stdoutTask = ReadBounded(_process.StandardOutput, _blobDirectory);
        var stderrTask = ReadBounded(_process.StandardError, _blobDirectory);

        try
        {
            await AwaitProcessAndOutput(stdoutTask, stderrTask).ConfigureAwait(false);
            var output = await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            var result = await FormatResult(
                    _process.ExitCode,
                    output[0],
                    output[1],
                    _blobDirectory,
                    _cancellation.Token)
                .ConfigureAwait(false);
            Volatile.Write(ref _completedResult, result);
            return result;
        }
        catch
        {
            Kill(_process);
            await _process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await DrainAndDelete(stdoutTask, stderrTask).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<ProcessResult> RunPseudoTerminal(PtyTranscript transcript)
    {
        Task? drainTask = null;

        try
        {
            var ready = await _process.StandardError.ReadLineAsync(_cancellation.Token).ConfigureAwait(false);

            if (!string.Equals(ready, BridgeReady, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    ready is null
                        ? "The PTY bridge exited before becoming ready."
                        : $"The PTY bridge failed to become ready: {ready}");
            }

            CloseSlave();
            var bridgeErrors = _process.StandardError.ReadToEndAsync(CancellationToken.None);
            drainTask = Task.Run(() => DrainPseudoTerminal(transcript), CancellationToken.None);
            await _process.WaitForExitAsync(_cancellation.Token).ConfigureAwait(false);
            await drainTask.ConfigureAwait(false);
            var bridgeError = await bridgeErrors.ConfigureAwait(false);

            if (bridgeError.Length > 0)
            {
                throw new InvalidOperationException($"The PTY bridge reported an error: {bridgeError.TrimEnd()}");
            }

            transcript.Complete();
            var (_, output) = transcript.ReadOutput(0);
            var result = await FormatResult(
                    _process.ExitCode,
                    output,
                    new ProcessOutput(string.Empty, string.Empty),
                    _blobDirectory,
                    CancellationToken.None)
                .ConfigureAwait(false);
            Volatile.Write(ref _completedResult, result);
            return result;
        }
        catch
        {
            Kill(_process);
            await _process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);

            if (drainTask is not null)
            {
                try
                {
                    await drainTask.ConfigureAwait(false);
                }
                catch
                {
                }
            }

            throw;
        }
        finally
        {
            CloseSlave();
            await CloseMaster().ConfigureAwait(false);
            transcript.Complete();
        }
    }

    private void DrainPseudoTerminal(PtyTranscript transcript)
    {
        var buffer = new byte[4096];

        while (true)
        {
            var read = LinuxPseudoTerminal.Read(_masterDescriptor, buffer);

            if (read == 0)
            {
                return;
            }

            transcript.Append(buffer.AsSpan(0, read));
        }
    }

    private async Task AwaitProcessAndOutput(params Task<ProcessOutput>[] outputs)
    {
        var exitTask = _process.WaitForExitAsync(_cancellation.Token);
        var pending = new List<Task>(outputs) { exitTask };

        while (pending.Count > 0)
        {
            var completed = await Task.WhenAny(pending).ConfigureAwait(false);
            _ = pending.Remove(completed);
            await completed.ConfigureAwait(false);
        }
    }

    private async Task AwaitCompletionForDisposal()
    {
        try
        {
            _ = await Result.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
        }
        catch
        {
        }
    }

    private async Task CloseMaster()
    {
        await _writeGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);

        try
        {
            if (_masterDescriptor >= 0 && Interlocked.Exchange(ref _masterClosed, 1) == 0)
            {
                LinuxPseudoTerminal.Close(_masterDescriptor);
            }
        }
        finally
        {
            _ = _writeGate.Release();
        }
    }

    private void CloseSlave()
    {
        if (_slaveDescriptor >= 0 && Interlocked.Exchange(ref _slaveClosed, 1) == 0)
        {
            LinuxPseudoTerminal.Close(_slaveDescriptor);
        }
    }
}
