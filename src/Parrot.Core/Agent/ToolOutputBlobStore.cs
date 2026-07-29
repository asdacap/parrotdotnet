using System.Text;
using Parrot.Process;

namespace Parrot.Agent;

internal sealed class ToolOutputBlobStore
{
    public const int MaximumInlineBytes = 64 * 1024;
    private const int MaximumNameAttempts = 16;
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);
    private readonly string _directory;
    private readonly Func<string> _nextName;

    public ToolOutputBlobStore(string directory)
        : this(directory, new HaikunatorNameGenerator().Next)
    {
    }

    internal ToolOutputBlobStore(string directory, Func<string> nextName)
    {
        _directory = Path.GetFullPath(directory);
        _nextName = nextName;
    }

    public static bool IsOversized(string output) => Encoding.UTF8.GetByteCount(output) > MaximumInlineBytes;

    public async Task<string> Persist(string output, CancellationToken cancellationToken)
    {
        EnsureDirectory(_directory);

        for (var attempt = 0; attempt < MaximumNameAttempts; attempt++)
        {
            var name = _nextName();

            if (!string.Equals(Path.GetFileName(name), name, StringComparison.Ordinal)
                || !name.EndsWith("-arse.dat", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The tool output blob name must be a safe -arse.dat basename.");
            }

            var path = Path.Combine(_directory, name);

            try
            {
                var options = new FileStreamOptions
                {
                    Access = FileAccess.Write,
                    Mode = FileMode.CreateNew,
                    Options = System.IO.FileOptions.Asynchronous,
                };

                if (!OperatingSystem.IsWindows())
                {
                    options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                }

                await using var stream = new FileStream(path, options);
                try
                {
                    await using var writer = new StreamWriter(stream, Utf8WithoutBom, leaveOpen: true);
                    await writer.WriteAsync(output.AsMemory(), cancellationToken).ConfigureAwait(false);
                    await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                    return $"Tool output exceeded 64 KiB and was saved to {path}. Use exec_command to read the file.";
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

        throw new IOException($"Could not create a unique tool output blob in '{_directory}'.");
    }

    private static void EnsureDirectory(string directory)
    {
        _ = Directory.CreateDirectory(directory);

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                directory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
