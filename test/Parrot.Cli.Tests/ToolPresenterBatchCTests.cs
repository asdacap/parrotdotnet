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
        yield return [new GlobToolPresenter(), "{\"pattern\":\"**/*.cs\",\"path\":\"src\"}", "glob \"**/*.cs\" in src"];
        yield return [new WebFetchToolPresenter(), "{\"url\":\"https://example.com/path\"}", "web fetch GET https://example.com/path"];
    }

    public static IEnumerable<object[]> PresenterInstances()
    {
        yield return [new GlobToolPresenter()];
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
        var call = new ToolCallPresentation(presenter.ToolName, argumentsJson);
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
        _ = await Assert.That(completed).Count().IsEqualTo(2);
    }

    [Test]
    public async Task Glob_uses_the_workspace_when_path_is_omitted()
    {
        IToolPresenter presenter = new GlobToolPresenter();
        var call = new ToolCallPresentation("glob", "{\"pattern\":\"src/**/*.cs\"}");

        var live = presenter.PresentLive(call, 0).Render(LiveContext).Lines.Select(line => line.Text).ToArray();

        _ = await Assert.That(live[0]).IsEqualTo("⠋ glob \"src/**/*.cs\"");
    }

    [Test]
    [Arguments(ToolTerminalStatus.Succeeded, true, "error: no such file or directory", "")]
    [Arguments(ToolTerminalStatus.Errored, false, "", "unexpected failure")]
    public async Task Failed_read_shows_the_full_request_as_yaml(
        ToolTerminalStatus status,
        bool resultPresent,
        string result,
        string error)
    {
        IToolPresenter presenter = new ReadToolPresenter();
        const string arguments = "{\"path\":\"src/App.cs\",\"offset\":12,\"limit\":3}";
        var call = new ToolCallPresentation("read", arguments);
        var terminal = new ToolTerminalPresentation(status, resultPresent, result, error);

        var live = presenter.PresentLive(call, 0).Render(LiveContext).Lines[0].Text;
        var item = presenter.PresentTerminal(call, terminal)
            ?? throw new InvalidOperationException("Terminal presentation missing.");
        var rendered = string.Join('\n', item.Render(ScrollbackContext));
        var expectedError = resultPresent ? result : error;
        var expectedBlock = $"path: \"src/App.cs\"\noffset: 12\nlimit: 3\n---\n{expectedError}";

        _ = await Assert.That(live).IsEqualTo("⠋ read src/App.cs");
        _ = await Assert.That(rendered).IsEqualTo($"✗ read src/App.cs\n  {expectedBlock.Replace("\n", "\n  ", StringComparison.Ordinal)}");
        _ = await Assert.That(rendered).Contains("✗ read src/App.cs");
        _ = await Assert.That(rendered).Contains("  path: \"src/App.cs\"");
        _ = await Assert.That(rendered).Contains("  offset: 12");
        _ = await Assert.That(rendered).Contains("  limit: 3");
        _ = await Assert.That(rendered).Contains("  ---");
        _ = await Assert.That(rendered).Contains($"  {expectedError}");
        _ = await Assert.That(rendered).DoesNotContain(arguments);
    }

    [Test]
    [Arguments(ToolTerminalStatus.Succeeded, true, "1: heading\n2: body", "", "✓")]
    [Arguments(ToolTerminalStatus.Cancelled, false, "", "", "■")]
    public async Task Non_failed_read_stays_compact(
        ToolTerminalStatus status,
        bool resultPresent,
        string result,
        string error,
        string expectedMarker)
    {
        IToolPresenter presenter = new ReadToolPresenter();
        var call = new ToolCallPresentation("read", "{\"path\":\"README.md\",\"offset\":2,\"limit\":4}");
        var terminal = new ToolTerminalPresentation(status, resultPresent, result, error);

        var item = presenter.PresentTerminal(call, terminal)
            ?? throw new InvalidOperationException("Terminal presentation missing.");
        var rendered = string.Join('\n', item.Render(ScrollbackContext));

        _ = await Assert.That(rendered).IsEqualTo($"{expectedMarker} read README.md");
        _ = await Assert.That(rendered).DoesNotContain("path:");
        _ = await Assert.That(rendered).DoesNotContain("offset:");
        _ = await Assert.That(rendered).DoesNotContain("limit:");
        _ = await Assert.That(rendered).DoesNotContain("---");
        _ = await Assert.That(rendered).DoesNotContain("1: heading");
    }

    [Test]
    [MethodDataSource(nameof(PresenterInstances))]
    public async Task Presenters_throw_for_malformed_arguments(IToolPresenter presenter)
    {
        var call = new ToolCallPresentation(presenter.ToolName, "{");
        var terminal = new ToolTerminalPresentation(
            ToolTerminalStatus.Succeeded,
            false,
            string.Empty,
            string.Empty);

        _ = await Assert.That(() => presenter.PresentLive(call, 0)).Throws<JsonException>();
        _ = await Assert.That(() => presenter.PresentTerminal(call, terminal)).Throws<JsonException>();
    }
}
