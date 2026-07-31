using System.Text;

namespace Parrot.Process;

internal sealed class ProcessOutput
{
    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);
    private readonly Encoding _sourceEncoding;
    private readonly long _temporaryOffset;
    private readonly long _characterLength;
    private readonly bool _ownsTemporaryFile;

    public ProcessOutput(string text, string temporaryPath)
        : this(
            text,
            temporaryPath,
            Utf8WithoutBom,
            0,
            temporaryPath.Length == 0 ? text.Length : CountCharacters(temporaryPath, Utf8WithoutBom),
            ownsTemporaryFile: true)
    {
    }

    internal ProcessOutput(
        string text,
        string temporaryPath,
        Encoding sourceEncoding,
        long temporaryOffset,
        long characterLength,
        bool ownsTemporaryFile)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(temporaryPath);
        ArgumentNullException.ThrowIfNull(sourceEncoding);
        ArgumentOutOfRangeException.ThrowIfNegative(temporaryOffset);
        ArgumentOutOfRangeException.ThrowIfNegative(characterLength);

        Text = text;
        TemporaryPath = temporaryPath;
        _sourceEncoding = sourceEncoding;
        _temporaryOffset = temporaryOffset;
        _characterLength = characterLength;
        _ownsTemporaryFile = ownsTemporaryFile;
    }

    public string Text { get; }

    public string TemporaryPath { get; }

    public bool Spilled => TemporaryPath.Length > 0;

    public long Length => Spilled ? _characterLength : Text.Length;

    public void CopyTo(TextWriter writer)
    {
        if (!Spilled)
        {
            writer.Write(Text);
            return;
        }

        using var stream = OpenTemporaryFile();

        if (_sourceEncoding.CodePage == Encoding.Unicode.CodePage)
        {
            CopyUtf16LittleEndianTo(stream, writer);
            return;
        }

        using var reader = new StreamReader(stream, _sourceEncoding, detectEncodingFromByteOrderMarks: false);
        var buffer = new char[4096];
        var remaining = _characterLength;

        while (remaining > 0)
        {
            var read = reader.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));

            if (read == 0)
            {
                throw new InvalidDataException("The process output ended before its declared character length.");
            }

            writer.Write(buffer.AsSpan(0, read));
            remaining -= read;
        }
    }

    public async Task CopyTo(TextWriter writer, CancellationToken cancellationToken)
    {
        if (!Spilled)
        {
            await writer.WriteAsync(Text.AsMemory(), cancellationToken).ConfigureAwait(false);
            return;
        }

        using var stream = OpenTemporaryFile();

        if (_sourceEncoding.CodePage == Encoding.Unicode.CodePage)
        {
            await CopyUtf16LittleEndianTo(stream, writer, cancellationToken).ConfigureAwait(false);
            return;
        }

        using var reader = new StreamReader(stream, _sourceEncoding, detectEncodingFromByteOrderMarks: false);
        var buffer = new char[4096];
        var remaining = _characterLength;

        while (remaining > 0)
        {
            var read = await reader
                .ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken)
                .ConfigureAwait(false);

            if (read == 0)
            {
                throw new InvalidDataException("The process output ended before its declared character length.");
            }

            await writer.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            remaining -= read;
        }
    }

    public void DeleteTemporaryFile()
    {
        if (Spilled && _ownsTemporaryFile)
        {
            File.Delete(TemporaryPath);
        }
    }

    private static long CountCharacters(string path, Encoding encoding)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: false);
        var buffer = new char[4096];
        long count = 0;

        while (true)
        {
            var read = reader.Read(buffer);

            if (read == 0)
            {
                return count;
            }

            count += read;
        }
    }

    private void CopyUtf16LittleEndianTo(Stream stream, TextWriter writer)
    {
        var bytes = new byte[8192];
        var characters = new char[4096];
        var remaining = _characterLength;

        while (remaining > 0)
        {
            var count = (int)Math.Min(characters.Length, remaining);
            stream.ReadExactly(bytes.AsSpan(0, count * 2));

            for (var index = 0; index < count; index++)
            {
                characters[index] = (char)(bytes[index * 2] | (bytes[(index * 2) + 1] << 8));
            }

            writer.Write(characters.AsSpan(0, count));
            remaining -= count;
        }
    }

    private async Task CopyUtf16LittleEndianTo(
        Stream stream,
        TextWriter writer,
        CancellationToken cancellationToken)
    {
        var bytes = new byte[8192];
        var characters = new char[4096];
        var remaining = _characterLength;

        while (remaining > 0)
        {
            var count = (int)Math.Min(characters.Length, remaining);
            await stream.ReadExactlyAsync(bytes.AsMemory(0, count * 2), cancellationToken).ConfigureAwait(false);

            for (var index = 0; index < count; index++)
            {
                characters[index] = (char)(bytes[index * 2] | (bytes[(index * 2) + 1] << 8));
            }

            await writer.WriteAsync(characters.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            remaining -= count;
        }
    }

    private FileStream OpenTemporaryFile() =>
        new(
            TemporaryPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete)
        {
            Position = _temporaryOffset,
        };
}
