using Parrot.Agent;
using Parrot.Llm;
using Parrot.Security;
using Parrot.Tools;

namespace Parrot.Core.Tests;

internal sealed class GrepToolTests : IDisposable
{
    private readonly string _workspace = Path.Combine(
        Path.GetTempPath(), "parrot-grep-tool-tests", Guid.NewGuid().ToString("n"));

    public GrepToolTests() => Directory.CreateDirectory(_workspace);

    public void Dispose()
    {
        if (Directory.Exists(_workspace))
        {
            Directory.Delete(_workspace, recursive: true);
        }
    }

    [Test]
    [Arguments("*.cs", "included.cs:1:find me\nsource/nested.cs:1:find me\n")]
    [Arguments("source/*.cs", "source/nested.cs:1:find me\n")]
    [Arguments("**/*.cs", "included.cs:1:find me\nsource/nested.cs:1:find me\n")]
    public async Task Include_glob_restricts_searched_files(
        string include,
        string expected,
        CancellationToken cancellationToken)
    {
        var source = Directory.CreateDirectory(Path.Combine(_workspace, "source")).FullName;
        await File.WriteAllTextAsync(Path.Combine(_workspace, "included.cs"), "find me", cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(source, "nested.cs"), "find me", cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(source, "excluded.txt"), "find me", cancellationToken);
        var tool = new GrepTool(new ToolWorkspace(_workspace));
        var arguments = $$"""{"pattern":"find me","include":"{{include}}"}""";

        var result = (await tool.Execute(
            new ToolInvocation("test-call", arguments),
            Turn(SecurityProfile.Compose(readOnly: false, [], [], [])),
            cancellationToken)).Text;

        _ = await Assert.That(result).IsEqualTo(expected);
    }

    [Test]
    public async Task Include_glob_does_not_filter_an_explicit_file(CancellationToken cancellationToken)
    {
        var file = Path.Combine(_workspace, "included.cs");
        await File.WriteAllTextAsync(file, "find me", cancellationToken);
        var tool = new GrepTool(new ToolWorkspace(_workspace));

        var result = (await tool.Execute(
            new ToolInvocation(
                "test-call",
                "{\"pattern\":\"find me\",\"path\":\"included.cs\",\"include\":\"*.txt\"}"),
            Turn(SecurityProfile.Compose(readOnly: false, [], [], [])),
            cancellationToken)).Text;

        _ = await Assert.That(result).IsEqualTo("included.cs:1:find me\n");
    }

    private static AgentTurnSelection Turn(SecurityProfile securityProfile)
    {
        var model = new ProviderModel(new UnusedProvider(), new LLMModel("model", "unused"));
        return new AgentTurnSelection(
            new ModelSelector(model.Selector),
            TestModels.Resolve(model),
            null,
            securityProfile);
    }
}
