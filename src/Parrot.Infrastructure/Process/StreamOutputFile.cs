using System.Text;

namespace Parrot.Process;

internal sealed class StreamOutputFile(string path, FileMode mode) : IDisposable, IAsyncDisposable
{
    private const UnixFileMode FilePermissions = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);
    private readonly StreamWriter _writer = OpenWriter(path, mode);
    private char? _trailingHighSurrogate;

    public static StreamOutputFile CreateNew(string path) => new(path, FileMode.CreateNew);

    public static StreamOutputFile OpenForAppend(string path) => new(path, FileMode.Append);

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

    private static StreamWriter OpenWriter(string path, FileMode mode)
    {
        var options = new FileStreamOptions
        {
            Access = FileAccess.Write,
            Mode = mode,
            Share = FileShare.ReadWrite,
            Options = FileOptions.Asynchronous,
        };

        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = FilePermissions;
        }

        return new StreamWriter(path, Utf8WithoutBom, options);
    }
}
