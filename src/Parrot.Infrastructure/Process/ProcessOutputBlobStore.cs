using System.Text;
using Parrot.Files;

namespace Parrot.Process;

internal sealed class ProcessOutputBlobStore
{
    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);
    private readonly string _directory;
    private readonly Func<string> _nextName;

    public ProcessOutputBlobStore(string directory)
        : this(directory, new HaikunatorNameGenerator().Next)
    {
    }

    internal ProcessOutputBlobStore(string directory, Func<string> nextName)
    {
        _directory = Path.GetFullPath(directory);
        _nextName = nextName;
    }

    public async Task<string> Persist(
        int exitCode,
        long elapsedMilliseconds,
        ProcessOutput stdout,
        ProcessOutput stderr,
        CancellationToken cancellationToken)
    {
        await using var stream = PrivateFile.CreateUnique(_directory, _nextName, "process output blob", FileOptions.Asynchronous);
        var path = stream.Name;
        try
        {
            await Write(stream, exitCode, elapsedMilliseconds, stdout, stderr, cancellationToken)
                .ConfigureAwait(false);
            return path;
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            File.Delete(path);
            throw;
        }
    }

    internal string PersistImmediately(int exitCode, long elapsedMilliseconds, ProcessOutput stdout)
    {
        var stream = PrivateFile.CreateUnique(_directory, _nextName, "process output blob", FileOptions.None);
        var path = stream.Name;
        try
        {
            using (stream)
            using (var writer = new StreamWriter(stream, Utf8WithoutBom))
            {
                writer.Write(ProcessResultFormatter.FormatCompletion(exitCode, elapsedMilliseconds));
                writer.Write("\n[stdout]\n");
                stdout.CopyTo(writer);
            }

            return path;
        }
        catch
        {
            File.Delete(path);
            throw;
        }
    }

    private static async Task Write(
        Stream stream,
        int exitCode,
        long elapsedMilliseconds,
        ProcessOutput stdout,
        ProcessOutput stderr,
        CancellationToken cancellationToken)
    {
        await using var writer = new StreamWriter(stream, Utf8WithoutBom, leaveOpen: true);
        await writer.WriteAsync(
            ProcessResultFormatter.FormatCompletion(exitCode, elapsedMilliseconds).AsMemory(),
            cancellationToken).ConfigureAwait(false);
        await WriteOutput(writer, "stdout", stdout, cancellationToken).ConfigureAwait(false);
        await WriteOutput(writer, "stderr", stderr, cancellationToken).ConfigureAwait(false);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteOutput(
        StreamWriter writer,
        string name,
        ProcessOutput output,
        CancellationToken cancellationToken)
    {
        if (output.Length == 0)
        {
            return;
        }

        await writer.WriteAsync($"\n[{name}]\n".AsMemory(), cancellationToken).ConfigureAwait(false);
        await output.CopyTo(writer, cancellationToken).ConfigureAwait(false);
    }
}
