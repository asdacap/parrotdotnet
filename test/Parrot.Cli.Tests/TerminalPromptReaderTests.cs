using Parrot.Cli.Enhanced;

namespace Parrot.Cli.Tests;

internal sealed class TerminalPromptReaderTests
{
    [Test]
    [Arguments(true, "openai", "openai")]
    [Arguments(false, "openai", "")]
    public async Task Prompt_reader_returns_the_typed_line_and_echoes_only_when_asked(
        bool echo, string typed, string expectedEcho, CancellationToken cancellationToken)
    {
        using var input = new StringReader(typed);
        using var output = new StringWriter();
        using var error = new StringWriter();
        var reader = new TerminalPromptReader(new TestTerminal(input, output, error, 80));

        var read = echo
            ? await reader.ReadLine(cancellationToken)
            : await reader.ReadSecret(cancellationToken);

        _ = await Assert.That(read).IsEqualTo(typed);
        _ = await Assert.That(output.ToString()).IsEqualTo(expectedEcho);
    }

    [Test]
    public async Task Prompt_reader_ends_a_line_at_end_of_input_and_a_secret_becomes_empty(
        CancellationToken cancellationToken)
    {
        using var input = new StringReader(string.Empty);
        using var output = new StringWriter();
        using var error = new StringWriter();
        var reader = new TerminalPromptReader(new TestTerminal(input, output, error, 80));

        _ = await Assert.That(await reader.ReadLine(cancellationToken)).IsNull();
        _ = await Assert.That(await reader.ReadSecret(cancellationToken)).IsEmpty();
    }
}
