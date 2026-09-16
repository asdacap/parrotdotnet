using Parrot.Store;

namespace Parrot.Core.Tests;

internal sealed class AgentOutputFileTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "parrot-agent-output-file-tests", Guid.NewGuid().ToString("n"));

    public AgentOutputFileTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Test]
    public async Task Eagerly_creates_a_concurrently_readable_file_that_a_reopened_session_appends_to(
        CancellationToken cancellationToken)
    {
        var first = new AgentOutputFile(_directory);

        _ = await Assert.That(first.OutputPath).IsEqualTo(Path.Combine(Path.GetFullPath(_directory), "output-arse.dat"));
        _ = await Assert.That(await File.ReadAllTextAsync(first.OutputPath, cancellationToken)).IsEmpty();

        await first.Append("first turn €");

        await using (var stream = new FileStream(first.OutputPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var reader = new StreamReader(stream))
        {
            _ = await Assert.That(await reader.ReadToEndAsync(cancellationToken)).IsEqualTo("first turn €");
        }

        await first.Close();

        var resumed = new AgentOutputFile(_directory);
        await resumed.Append("\nsecond turn");
        await resumed.Close();

        _ = await Assert.That(await File.ReadAllTextAsync(first.OutputPath, cancellationToken))
            .IsEqualTo("first turn €\nsecond turn");
    }
}
