using System.Text;
using Parrot.Files;
using Parrot.Process;

namespace Parrot.Store;

internal sealed class ToolOutputBlobStore
{
    public const int MaximumInlineBytes = 64 * 1024;
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
        await using var stream = PrivateFile.CreateUnique(_directory, _nextName, "tool output blob", FileOptions.Asynchronous);
        var path = stream.Name;
        try
        {
            await using var writer = new StreamWriter(stream, Utf8WithoutBom, leaveOpen: true);
            await writer.WriteAsync(output.AsMemory(), cancellationToken).ConfigureAwait(false);
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            return $"Tool output exceeded 64 KiB and was saved to {path}.";
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            File.Delete(path);
            throw;
        }
    }
}
