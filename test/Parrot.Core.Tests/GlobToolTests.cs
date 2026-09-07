using Parrot.Agent;
using Parrot.Llm;
using Parrot.Security;
using Parrot.Tools;

namespace Parrot.Core.Tests;

internal sealed class GlobToolTests : IDisposable
{
    private const int ResultLimit = 1000;
    private readonly string _workspace = Path.Combine(
        Path.GetTempPath(), "parrot-glob-tool-tests", Guid.NewGuid().ToString("n"));

    public GlobToolTests() => Directory.CreateDirectory(_workspace);

    public void Dispose()
    {
        if (Directory.Exists(_workspace))
        {
            Directory.Delete(_workspace, recursive: true);
        }
    }

    [Test]
    public async Task Reports_when_result_limit_stops_search(CancellationToken cancellationToken)
    {
        CreateFiles(ResultLimit + 1);
        var result = await Execute(cancellationToken);

        _ = await Assert.That(result.Split('\n').Count(line => line.EndsWith(".txt", StringComparison.Ordinal)))
            .IsEqualTo(ResultLimit);
        _ = await Assert.That(result).EndsWith("[glob results truncated: result limit reached]\n");
    }

    [Test]
    public async Task Does_not_report_truncation_when_search_completes_at_result_limit(
        CancellationToken cancellationToken)
    {
        CreateFiles(ResultLimit);
        var result = await Execute(cancellationToken);

        _ = await Assert.That(result).DoesNotContain("[glob results truncated:");
    }

    private void CreateFiles(int count)
    {
        for (var index = 0; index < count; index++)
        {
            File.WriteAllText(Path.Combine(_workspace, $"file-{index:D4}.txt"), string.Empty);
        }
    }

    private async Task<string> Execute(CancellationToken cancellationToken)
    {
        ITool tool = new GlobTool(new ToolWorkspace(_workspace));
        var model = new ProviderModel(new UnusedProvider(), new LLMModel("model", "unused"));
        return (await tool.Execute(
            new ToolInvocation("test-call", "{\"pattern\":\"*.txt\"}"),
            new AgentTurnSelection(
                new ModelSelector(model.Selector),
                TestModels.Resolve(model),
                new TestProfileFixture().Mode,
                SecurityProfile.Compose(readOnly: false, [], [], [])),
            cancellationToken)).Text;
    }
}
