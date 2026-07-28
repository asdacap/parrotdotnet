using System.Text;

namespace Parrot.Process;

internal sealed class ProcessOutputBlobStore
{
    private const int MaximumNameAttempts = 16;
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
        ProcessOutput stdout,
        ProcessOutput stderr,
        CancellationToken cancellationToken)
    {
        EnsureDirectory(_directory);

        for (var attempt = 0; attempt < MaximumNameAttempts; attempt++)
        {
            var name = _nextName();

            if (!string.Equals(Path.GetFileName(name), name, StringComparison.Ordinal)
                || !name.EndsWith("-arse.dat", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The process output blob name must be a safe -arse.dat basename.");
            }

            var path = Path.Combine(_directory, name);

            try
            {
                var fileOptions = new FileStreamOptions
                {
                    Access = FileAccess.Write,
                    Mode = FileMode.CreateNew,
                    Options = System.IO.FileOptions.Asynchronous,
                };

                if (!OperatingSystem.IsWindows())
                {
                    fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                }

                await using var stream = new FileStream(path, fileOptions);

                try
                {
                    await Write(stream, exitCode, stdout, stderr, cancellationToken).ConfigureAwait(false);
                    return path;
                }
                catch
                {
                    await stream.DisposeAsync().ConfigureAwait(false);
                    File.Delete(path);
                    throw;
                }
            }
            catch (IOException) when (File.Exists(path))
            {
            }
        }

        throw new IOException($"Could not create a unique process output blob in '{_directory}'.");
    }

    internal static void EnsureDirectory(string directory)
    {
        _ = Directory.CreateDirectory(directory);

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                directory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static async Task Write(
        Stream stream,
        int exitCode,
        ProcessOutput stdout,
        ProcessOutput stderr,
        CancellationToken cancellationToken)
    {
        await using var writer = new StreamWriter(stream, Utf8WithoutBom, leaveOpen: true);
        await writer.WriteAsync(
            $"Process exited with code {exitCode}".AsMemory(), cancellationToken).ConfigureAwait(false);
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
