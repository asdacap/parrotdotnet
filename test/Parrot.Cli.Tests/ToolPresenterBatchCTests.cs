using System.Text.Json;
using Parrot.Cli.Enhanced;
using Parrot.Cli.Enhanced.Tools;

namespace Parrot.Cli.Tests;

internal sealed class ToolPresenterBatchCTests
{
    private static readonly LiveBufferRenderContext LiveContext = new(32_768, new TerminalPalette(false));
    private static readonly ScrollbackRenderContext ScrollbackContext = new(32_768, new TerminalPalette(false));

    public static IEnumerable<object[]> Presenters()
    {
        yield return [new GlobToolPresenter(), "{\"pattern\":\"**/*.cs\",\"path\":\"src\"}", "main: glob \"**/*.cs\" in src"];
        yield return [new GrepToolPresenter(), "{\"pattern\":\"TODO\",\"path\":\"src\",\"include\":\"**/*.cs\"}", "main: grep \"TODO\" in src matching \"**/*.cs\""];
        yield return [new ReadToolPresenter(), "{\"path\":\"README.md\"}", "main: read README.md"];
        yield return [new WebFetchToolPresenter(), "{\"url\":\"https://example.com/path\"}", "main: web fetch GET https://example.com/path"];
    }

    public static IEnumerable<object[]> PresenterInstances()
    {
        yield return [new GlobToolPresenter()];
        yield return [new GrepToolPresenter()];
        yield return [new ReadToolPresenter()];
        yield return [new WebFetchToolPresenter()];
    }

    [Test]
    [MethodDataSource(nameof(Presenters))]
    public async Task Presenters_render_schema_labels_and_report_tool_errors(
        IToolPresenter presenter,
        string argumentsJson,
        string expectedLabel)
    {
        var call = new ToolCallPresentation("main", presenter.ToolName, argumentsJson);
        var terminal = new ToolTerminalPresentation(
            ToolTerminalStatus.Succeeded,
            true,
            "error: tool failure",
            string.Empty);

        var live = presenter.PresentLive(call, 0).Render(LiveContext).Lines.Select(line => line.Text).ToArray();
        var item = presenter.PresentTerminal(call, terminal)
            ?? throw new InvalidOperationException("Presenter did not render terminal output.");
        var completed = item.Render(ScrollbackContext);

        _ = await Assert.That(live[0]).IsEqualTo($"⠋ {expectedLabel}");
        _ = await Assert.That(completed[0]).IsEqualTo($"✗ {expectedLabel}");
        _ = await Assert.That(completed[1]).IsEqualTo("  error: tool failure");
        _ = await Assert.That(((IToolPresentationValue)item).Report.Block.Kind).IsEqualTo(ToolBlockKind.Error);
    }

    [Test]
    public async Task Glob_uses_the_workspace_when_path_is_omitted()
    {
        var presenter = new GlobToolPresenter();
        var call = new ToolCallPresentation("main", "glob", "{\"pattern\":\"src/**/*.cs\"}");

        var live = presenter.PresentLive(call, 0).Render(LiveContext).Lines.Select(line => line.Text).ToArray();

        _ = await Assert.That(live[0]).IsEqualTo("⠋ main: glob \"src/**/*.cs\"");
    }

    [Test]
    public async Task Successful_read_hides_result_lines()
    {
        var presenter = new ReadToolPresenter();
        var call = new ToolCallPresentation("main", "read", "{\"path\":\"README.md\"}");
        var terminal = new ToolTerminalPresentation(
            ToolTerminalStatus.Succeeded,
            true,
            "1: heading\n2: body",
            string.Empty);

        var item = presenter.PresentTerminal(call, terminal);
        var lines = item.Render(ScrollbackContext);

        _ = await Assert.That(string.Join('\n', lines)).IsEqualTo("✓ main: read README.md");
        _ = await Assert.That(string.Join('\n', lines)).DoesNotContain("1: heading");
        _ = await Assert.That(string.Join('\n', lines)).DoesNotContain("2: body");
    }

    [Test]
    [MethodDataSource(nameof(PresenterInstances))]
    public async Task Presenters_throw_for_malformed_arguments(IToolPresenter presenter)
    {
        var call = new ToolCallPresentation("main", presenter.ToolName, "{");
        var terminal = new ToolTerminalPresentation(
            ToolTerminalStatus.Succeeded,
            false,
            string.Empty,
            string.Empty);

        _ = await Assert.That(() => presenter.PresentLive(call, 0)).Throws<JsonException>();
        _ = await Assert.That(() => presenter.PresentTerminal(call, terminal)).Throws<JsonException>();
    }
}
