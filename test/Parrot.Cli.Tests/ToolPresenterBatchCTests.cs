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
        yield return [new GitDiffToolPresenter(), "{\"target\":\"base\",\"ref\":\"origin/main\"}", "main: git diff base origin/main"];
        yield return [new GlobToolPresenter(), "{\"pattern\":\"src/**/*.cs\"}", "main: glob \"src/**/*.cs\""];
        yield return [new GrepToolPresenter(), "{\"pattern\":\"TODO\",\"path\":\"src\"}", "main: grep \"TODO\" in src"];
        yield return [new ReadToolPresenter(), "{\"path\":\"README.md\"}", "main: read README.md"];
        yield return [new WebFetchToolPresenter(), "{\"url\":\"https://example.com/path\"}", "main: web fetch GET https://example.com/path"];
    }

    public static IEnumerable<object[]> PresenterInstances()
    {
        yield return [new GitDiffToolPresenter()];
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
