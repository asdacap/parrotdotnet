using Parrot.Cli.Enhanced;
using Parrot.Cli.Enhanced.Tools;

namespace Parrot.Cli.Tests;

internal sealed class StatusToolPresenterTests
{
    [Test]
    [Arguments(32)]
    [Arguments(512)]
    public async Task Status_output_preserves_all_lines_and_wrapping(int columns)
    {
        var registry = new ToolPresenterRegistry([new StatusToolPresenter()], new GenericToolPresenter());
        var output = string.Join('\n', Enumerable.Range(1, 200)
            .Select(static index => $"Model {index}: {new string('x', 100)}\u001b"));
        var context = new ScrollbackRenderContext(columns, new TerminalPalette(false));
        var rendered = (registry.PresentTerminal(
            new ToolCallPresentation("main", "status", "{}"),
            new ToolTerminalPresentation(ToolTerminalStatus.Succeeded, true, output, string.Empty))
            ?? throw new InvalidOperationException("Terminal presentation missing."))
            .Render(context);
        var expected = TerminalText.Layout("✓ main: tool call status", columns)
            .Concat(output.Replace("\u001b", string.Empty, StringComparison.Ordinal).Split('\n')
                .SelectMany(line => TerminalText.Layout($"  {line}", columns)));

        _ = await Assert.That(string.Join('\n', rendered)).IsEqualTo(string.Join('\n', expected));
        _ = await Assert.That(rendered.Count).IsGreaterThan(200);

        var generic = (registry.PresentTerminal(
            new ToolCallPresentation("main", "other", "{}"),
            new ToolTerminalPresentation(ToolTerminalStatus.Succeeded, true, output, string.Empty))
            ?? throw new InvalidOperationException("Terminal presentation missing."))
            .Render(context);
        _ = await Assert.That(generic.Count).IsLessThanOrEqualTo(10);
    }
}
