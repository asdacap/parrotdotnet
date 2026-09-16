using Parrot.Process;

namespace Parrot.Store;

internal sealed class AgentOutputFile
{
    private readonly StreamOutputFile _file;

    public AgentOutputFile(string blobDirectory)
    {
        var directory = Directory.CreateDirectory(blobDirectory).FullName;
        OutputPath = Path.Combine(directory, "output-arse.dat");
        _file = StreamOutputFile.OpenForAppend(OutputPath);
    }

    public string OutputPath { get; }

    public Task Append(string text) => _file.Append(text.AsMemory());

    public ValueTask Close() => _file.Complete();
}
