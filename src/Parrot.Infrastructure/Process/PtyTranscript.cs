using System.Text;

namespace Parrot.Process;

internal sealed class PtyTranscript(string directory) : IDisposable
{
    internal const int MaximumReadCharacters = 64 << 10;
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly UnicodeEncoding Utf16LittleEndian = new(
        bigEndian: false,
        byteOrderMark: false,
        throwOnInvalidBytes: false);

    private readonly Lock _gate = new();
    private readonly Decoder _decoder = Utf8.GetDecoder();
    private readonly string _directory = Path.GetFullPath(directory);
    private FileStream? _stream;
    private string _temporaryPath = string.Empty;
    private long _length;
    private bool _completed;
    private bool _disposed;

    public long Length
    {
        get
        {
            lock (_gate)
            {
                return _length;
            }
        }
    }

    public void Append(ReadOnlySpan<byte> bytes)
    {
        lock (_gate)
        {
            ThrowIfDisposed();

            if (_completed)
            {
                throw new InvalidOperationException("The PTY transcript is complete.");
            }

            Append(bytes, flush: false);
        }
    }

    public void Complete()
    {
        lock (_gate)
        {
            ThrowIfDisposed();

            if (_completed)
            {
                return;
            }

            Append([], flush: true);
            _completed = true;
            _stream?.Flush();
        }
    }

    public (long Cursor, string Text) Read(long offset)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            ValidateOffset(offset);
            return Read(offset, MaximumReadCharacters);
        }
    }

    public (long Cursor, ProcessOutput Output) ReadOutput(long offset)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            ValidateOffset(offset);

            if (!_completed)
            {
                throw new InvalidOperationException("The PTY transcript is not complete.");
            }

            if (offset == _length)
            {
                return (offset, new ProcessOutput(string.Empty, string.Empty));
            }

            _stream?.Flush();
            var remaining = _length - offset;

            if (remaining <= MaximumReadCharacters)
            {
                var (cursor, text) = Read(offset, MaximumReadCharacters);
                return (cursor, new ProcessOutput(text, string.Empty));
            }

            return (
                _length,
                new ProcessOutput(
                    string.Empty,
                    _temporaryPath,
                    Utf16LittleEndian,
                    checked(offset * 2),
                    remaining,
                    ownsTemporaryFile: false));
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _stream?.Dispose();
            _stream = null;

            if (_temporaryPath.Length > 0)
            {
                File.Delete(_temporaryPath);
            }
        }
    }

    private (long Cursor, string Text) Read(long offset, int maximumCharacters)
    {
        if (offset == _length)
        {
            return (offset, string.Empty);
        }

        _stream?.Flush();
        var count = (int)Math.Min(maximumCharacters, _length - offset);
        var characters = ReadCharacters(offset, count);

        if (offset + count < _length && char.IsHighSurrogate(characters[^1]))
        {
            var expanded = new char[count + 1];
            characters.CopyTo(expanded, 0);
            expanded[^1] = ReadCharacters(offset + count, 1)[0];
            characters = expanded;
            count++;
        }

        return (offset + count, new string(characters));
    }

    private void Append(ReadOnlySpan<byte> bytes, bool flush)
    {
        var characters = new char[Utf8.GetMaxCharCount(bytes.Length)];
        _decoder.Convert(
            bytes,
            characters,
            flush,
            out var bytesUsed,
            out var charactersUsed,
            out var completed);

        if (bytesUsed != bytes.Length || !completed)
        {
            throw new InvalidOperationException("The PTY transcript decoder did not consume its input.");
        }

        if (charactersUsed == 0)
        {
            return;
        }

        var encoded = new byte[checked(charactersUsed * 2)];

        for (var index = 0; index < charactersUsed; index++)
        {
            encoded[index * 2] = (byte)characters[index];
            encoded[(index * 2) + 1] = (byte)(characters[index] >> 8);
        }

        EnsureStream().Write(encoded);
        _length += charactersUsed;
    }

    private char[] ReadCharacters(long offset, int count)
    {
        var bytes = new byte[checked(count * 2)];
        using var stream = new FileStream(
            _temporaryPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        stream.Position = checked(offset * 2);
        stream.ReadExactly(bytes);
        var characters = new char[count];

        for (var index = 0; index < count; index++)
        {
            characters[index] = (char)(bytes[index * 2] | (bytes[(index * 2) + 1] << 8));
        }

        return characters;
    }

    private FileStream EnsureStream()
    {
        if (_stream is not null)
        {
            return _stream;
        }

        ProcessOutputBlobStore.EnsureDirectory(_directory);
        _temporaryPath = Path.Combine(_directory, $".process-{Guid.NewGuid():n}.tmp");
        var options = new FileStreamOptions
        {
            Access = FileAccess.Write,
            Mode = FileMode.CreateNew,
            Share = FileShare.Read | FileShare.Delete,
        };

        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        try
        {
            _stream = new FileStream(_temporaryPath, options);
            return _stream;
        }
        catch
        {
            File.Delete(_temporaryPath);
            _temporaryPath = string.Empty;
            throw;
        }
    }

    private void ValidateOffset(long offset)
    {
        if (offset < 0 || offset > _length)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
