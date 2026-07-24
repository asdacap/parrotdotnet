namespace Parrot.Process;

internal sealed class ProcessOutput(string text, string temporaryPath)
{
    public string Text { get; } = text;

    public string TemporaryPath { get; } = temporaryPath;

    public bool Spilled => TemporaryPath.Length > 0;

    public long Length => Spilled ? new FileInfo(TemporaryPath).Length : Text.Length;

    public async Task CopyTo(TextWriter writer, CancellationToken cancellationToken)
    {
        if (!Spilled)
        {
            await writer.WriteAsync(Text.AsMemory(), cancellationToken).ConfigureAwait(false);
            return;
        }

        using var reader = new StreamReader(TemporaryPath);
        var buffer = new char[4096];

        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);

            if (read == 0)
            {
                return;
            }

            await writer.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }

    public void DeleteTemporaryFile()
    {
        if (Spilled)
        {
            File.Delete(TemporaryPath);
        }
    }
}
