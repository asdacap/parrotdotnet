using System.Text.Json;
using Parrot.Cli.Enhanced;
using Parrot.Cli.Enhanced.Tools;

namespace Parrot.Cli.Tests;

internal sealed class WriteEditToolPresenterTests
{
    private static readonly LiveBufferRenderContext LiveContext = new(32_768, new TerminalPalette(false));
    private static readonly ScrollbackRenderContext ScrollbackContext = new(80, new TerminalPalette(false));

    public static IEnumerable<object[]> Presenters()
    {
        yield return
        [
            new WriteToolPresenter(),
            "{\"path\":\"src/file.txt\",\"content\":\"secret content\"}",
            "main: write src/file.txt",
            "secret content",
        ];
        yield return
        [
            new EditToolPresenter(),
            "{\"path\":\"src/file.txt\",\"old_string\":\"secret search\",\"new_string\":\"secret replacement\",\"replace_all\":true}",
            "main: edit src/file.txt",
            "secret search",
        ];
    }

    public static IEnumerable<object[]> PresenterInstances()
    {
        yield return [new WriteToolPresenter()];
        yield return [new EditToolPresenter()];
    }

    [Test]
    [MethodDataSource(nameof(Presenters))]
    public async Task Presenters_label_only_the_path(
        IToolPresenter presenter,
        string arguments,
        string expectedLabel,
        string hiddenValue)
    {
        var call = new ToolCallPresentation("main", presenter.ToolName, arguments);
        var terminal = new ToolTerminalPresentation(
            ToolTerminalStatus.Succeeded,
            true,
            "No changes made.",
            string.Empty);

        var live = presenter.PresentLive(call, 0).Render(LiveContext).Lines.Select(line => line.Text);
        var item = presenter.PresentTerminal(call, terminal)
            ?? throw new InvalidOperationException("Presenter did not render terminal output.");
        var completed = item.Render(ScrollbackContext);

        _ = await Assert.That(string.Join('\n', live)).Contains(expectedLabel);
        _ = await Assert.That(string.Join('\n', completed)).Contains(expectedLabel);
        _ = await Assert.That(string.Join('\n', live)).DoesNotContain(hiddenValue);
        _ = await Assert.That(string.Join('\n', completed)).DoesNotContain(hiddenValue);
        _ = await Assert.That(completed).Count().IsEqualTo(1);
        _ = await Assert.That(completed[0]).IsEqualTo($"✓ {expectedLabel}");
    }

    [Test]
    [MethodDataSource(nameof(Presenters))]
    public async Task Presenters_render_unified_diff_results(
        IToolPresenter presenter,
        string arguments,
        string expectedLabel,
        string hiddenValue)
    {
        const string diff = "--- a/src/file.txt\n+++ b/src/file.txt\n@@ -1,1 +1,1 @@\n-old\n+new\n";
        var call = new ToolCallPresentation("main", presenter.ToolName, arguments);
        var terminal = new ToolTerminalPresentation(ToolTerminalStatus.Succeeded, true, diff, string.Empty);

        var item = presenter.PresentTerminal(call, terminal)
            ?? throw new InvalidOperationException("Presenter did not render terminal output.");
        var rendered = item.Render(ScrollbackContext);

        _ = await Assert.That(rendered[0]).Contains(expectedLabel);
        _ = await Assert.That(string.Join('\n', rendered)).Contains("src/file.txt");
        _ = await Assert.That(string.Join('\n', rendered)).Contains("@@ -1,1 +1,1 @@");
        _ = await Assert.That(string.Join('\n', rendered)).Contains("1 -old");
        _ = await Assert.That(string.Join('\n', rendered)).Contains("1 +new");
        _ = await Assert.That(string.Join('\n', rendered)).DoesNotContain(hiddenValue);
    }

    [Test]
    [MethodDataSource(nameof(Presenters))]
    public async Task Presenters_report_error_results(
        IToolPresenter presenter,
        string arguments,
        string expectedLabel,
        string hiddenValue)
    {
        var call = new ToolCallPresentation("main", presenter.ToolName, arguments);
        var terminal = new ToolTerminalPresentation(
            ToolTerminalStatus.Succeeded,
            true,
            "error: operation failed",
            string.Empty);

        var item = presenter.PresentTerminal(call, terminal)
            ?? throw new InvalidOperationException("Presenter did not render terminal output.");
        var rendered = item.Render(ScrollbackContext);

        _ = await Assert.That(rendered[0]).IsEqualTo($"✗ {expectedLabel}");
        _ = await Assert.That(rendered).Count().IsEqualTo(2);
        _ = await Assert.That(rendered[1]).IsEqualTo("  error: operation failed");
        _ = await Assert.That(string.Join('\n', rendered)).Contains("error: operation failed");
        _ = await Assert.That(string.Join('\n', rendered)).DoesNotContain(hiddenValue);
    }

    [Test]
    [MethodDataSource(nameof(PresenterInstances))]
    public async Task Presenters_throw_for_malformed_arguments(IToolPresenter presenter)
    {
        var call = new ToolCallPresentation("main", presenter.ToolName, "{");
        var terminal = new ToolTerminalPresentation(ToolTerminalStatus.Succeeded, false, string.Empty, string.Empty);

        _ = await Assert.That(() => presenter.PresentLive(call, 0)).Throws<JsonException>();
        _ = await Assert.That(() => presenter.PresentTerminal(call, terminal)).Throws<JsonException>();
    }
}
