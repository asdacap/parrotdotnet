using System.Text;
using Parrot.Agent;
using Parrot.Llm;
using Parrot.Security;
using Parrot.Tools;

namespace Parrot.Core.Tests;

internal sealed class ReadToolTests : IDisposable
{
    private readonly string _workspace = Path.Combine(
        Path.GetTempPath(), "parrot-read-tool-tests", Guid.NewGuid().ToString("n"));

    public ReadToolTests() => Directory.CreateDirectory(_workspace);

    public void Dispose()
    {
        if (Directory.Exists(_workspace))
        {
            Directory.Delete(_workspace, recursive: true);
        }
    }

    [Test]
    public async Task Reads_a_file_while_its_writer_allows_concurrent_reading(CancellationToken cancellationToken)
    {
        var path = Path.Combine(_workspace, "live.txt");
        await using var writer = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite);
        await writer.WriteAsync("live output"u8.ToArray(), cancellationToken);
        await writer.FlushAsync(cancellationToken);

        var result = await Execute("{\"path\":\"live.txt\"}", cancellationToken);

        _ = await Assert.That(result).IsEqualTo("1: live output\ntotal lines in file: 1\n");
    }

    [Test]
    public async Task Reports_total_lines_for_a_requested_range(CancellationToken cancellationToken)
    {
        await File.WriteAllTextAsync(
            Path.Combine(_workspace, "lines.txt"),
            "alpha\nbeta\ngamma\n",
            cancellationToken);

        var result = await Execute("{\"path\":\"lines.txt\",\"offset\":2,\"limit\":1}", cancellationToken);

        _ = await Assert.That(result).IsEqualTo("2: beta\ntotal lines in file: 3\n");
        _ = await Assert.That(result).DoesNotContain("sha256:");
    }

    [Test]
    public async Task Reports_zero_lines_for_an_empty_file(CancellationToken cancellationToken)
    {
        await File.WriteAllTextAsync(Path.Combine(_workspace, "empty.txt"), string.Empty, cancellationToken);

        var result = await Execute("{\"path\":\"empty.txt\"}", cancellationToken);

        _ = await Assert.That(result).IsEqualTo("total lines in file: 0\n");
    }

    [Test]
    public async Task Reports_total_lines_after_output_truncation(CancellationToken cancellationToken)
    {
        const int totalLines = 2001;
        var content = new StringBuilder();
        for (var lineNumber = 0; lineNumber < totalLines; lineNumber++)
        {
            _ = content.Append('x', 600).Append('\n');
        }

        await File.WriteAllTextAsync(Path.Combine(_workspace, "large.txt"), content.ToString(), cancellationToken);

        var result = await Execute("{\"path\":\"large.txt\",\"limit\":2000}", cancellationToken);

        _ = await Assert.That(result).EndsWith("[output truncated]\ntotal lines in file: 2001\n");
    }

    private static AgentTurnSelection Turn()
    {
        var model = new ProviderModel(new UnusedProvider(), new LLMModel("model", "unused"));
        return new AgentTurnSelection(
            new ModelSelector(model.Selector),
            TestModels.Resolve(model),
            TestModels.Profile(),
            SecurityProfile.Compose(readOnly: false, [], [], []));
    }

    private async Task<string> Execute(string arguments, CancellationToken cancellationToken)
    {
        var tool = new ReadTool(new ToolWorkspace(_workspace));
        return (await tool.Execute(
            new ToolInvocation("test-call", arguments),
            Turn(),
            cancellationToken)).Text;
    }
}
