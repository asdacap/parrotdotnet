using System.Text.RegularExpressions;
using Parrot.Protocol;

namespace Parrot.Cli.Tests;

internal sealed class EnhancedCliTests
{
    [Test]
    public async Task Typed_turn_events_render_cumulative_text_and_sanitize_terminal_content(
        CancellationToken cancellationToken)
    {
        var (completed, output, error) = await Render(
            [
                new Event
                {
                    Id = "admitted-before-turn",
                    InputAdmitted = new InputAdmitted { Content = "already visible" },
                },
                new Event { Id = "start", TurnStarted = new TurnStarted { Model = "model" } },
                new Event
                {
                    Id = "queued",
                    InputAdmitted = new InputAdmitted { Content = "next\u001b[2J\tline" },
                },
                new Event { Id = "thought-1", ReasoningChunk = new ReasoningChunk { Fragment = "think" } },
                new Event
                {
                    Id = "thought-2",
                    ReasoningChunk = new ReasoningChunk { Fragment = "\u001b]0;title\n" },
                },
                new Event { Id = "text-1", TextChunk = new TextChunk { Fragment = "abcdefgh" } },
                new Event { Id = "text-2", TextChunk = new TextChunk { Fragment = "ij" } },
                new Event
                {
                    Id = "tool-chunk",
                    ToolCallChunk = new ToolCallChunk { ToolName = "unrendered" },
                },
                new Event
                {
                    Id = "tool-started",
                    ToolStarted = new ToolStarted { ToolCallId = "call-1", ToolName = "shell\u001b[31m" },
                },
                new Event
                {
                    Id = "tool-finished",
                    ToolFinished = new ToolFinished { ToolCallId = "call-1", ToolName = "shell\u001b[31m" },
                },
                new Event
                {
                    Id = "tool-cancelled",
                    ToolCancelled = new ToolCancelled { ToolCallId = "call-2", ToolName = "read\u001b[2J" },
                },
                new Event
                {
                    Id = "tool-error",
                    ToolError = new ToolError
                    {
                        ToolCallId = "call-3",
                        ToolName = "write\u001b[31m",
                        Message = "denied\u001b[2J",
                    },
                },
                new Event
                {
                    Id = "agent-started",
                    AgentStarted = new AgentStarted { Name = "explorer\u001b[31m" },
                },
                new Event
                {
                    Id = "agent-finished",
                    AgentFinished = new AgentFinished { Name = "explorer\u001b[31m" },
                },
                new Event
                {
                    Id = "agent-failed",
                    AgentFailed = new AgentFailed { Name = "reviewer\u001b[31m", Message = "boom\u001b[2J" },
                },
                new Event { Id = "text-3", TextChunk = new TextChunk { Fragment = "tail" } },
                new Event
                {
                    Id = "ended",
                    TurnEnded = new TurnEnded { FinishReason = "stop\u001b[2J", InputTokens = 3, OutputTokens = 4 },
                },
            ],
            cancellationToken);

        _ = await Assert.That(completed).IsTrue();
        _ = await Assert.That(error).IsEmpty();
        _ = await Assert.That(output).DoesNotContain("already visible");
        _ = await Assert.That(output).Contains("  queued: next[2J    line");
        _ = await Assert.That(output).Contains("think]0;title\n");
        _ = await Assert.That(output).Contains("abcdefgh\n");
        _ = await Assert.That(output).Contains("ij\n");
        _ = await Assert.That(output).DoesNotContain("unrendered");
        _ = await Assert.That(output).Contains("  * shell[31m started");
        _ = await Assert.That(output).Contains("  + shell[31m finished");
        _ = await Assert.That(output).Contains("  - read[2J cancelled");
        _ = await Assert.That(output).Contains("  ! write[31m: denied[2J");
        _ = await Assert.That(output).Contains("  * agent explorer[31m started");
        _ = await Assert.That(output).Contains("  + agent explorer[31m finished");
        _ = await Assert.That(output).Contains("  ! agent reviewer[31m: boom[2J");
        _ = await Assert.That(output).Contains("tail\n");
        _ = await Assert.That(output).Contains("  stop[2J - 3 in / 4 out");
        _ = await Assert.That(Count(output, "abcdefgh\n")).IsEqualTo(1);
        _ = await Assert.That(UntrustedEscape(output)).IsFalse();
        _ = await Assert.That(output).DoesNotContain("\u001b[?1049");
    }

    [Test]
    public async Task Failure_commits_the_live_suffix_and_reports_sanitized_error(
        CancellationToken cancellationToken)
    {
        var (completed, output, error) = await Render(
            [
                new Event { Id = "start", TurnStarted = new TurnStarted { Model = "model" } },
                new Event { Id = "text", TextChunk = new TextChunk { Fragment = "partial" } },
                new Event
                {
                    Id = "failed",
                    TurnFailed = new TurnFailed { Message = "bad\u001b[2J\trequest" },
                },
            ],
            cancellationToken);

        _ = await Assert.That(completed).IsFalse();
        _ = await Assert.That(output).Contains("partial\n");
        _ = await Assert.That(error).Contains("  bad[2J    request");
        _ = await Assert.That(error).DoesNotContain("\u001b[2J");
    }

    private static async Task<(bool Completed, string Output, string Error)> Render(
        IEnumerable<Event> events,
        CancellationToken cancellationToken)
    {
        var stream = new ChannelStreamWriter<Event>();
        foreach (var published in events)
        {
            await stream.WriteAsync(published, cancellationToken);
        }

        stream.Complete();

        using var output = new StringWriter();
        using var error = new StringWriter();
        var completed = await EnhancedCli.RenderTurn(stream.Reader, output, error, static () => 8, cancellationToken);
        return (completed, output.ToString(), error.ToString());
    }

    private static bool UntrustedEscape(string output)
    {
        var withoutOwnedAnsi = Regex.Replace(
            output,
            "\\x1b(?:\\[\\?25[lh]|\\[2K|\\[[0-9]+A|\\[(?:0|2|31|32|36)m)",
            string.Empty,
            RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));
        return withoutOwnedAnsi.Contains('\u001b', StringComparison.Ordinal);
    }

    private static int Count(string value, string part)
    {
        var count = 0;
        var start = 0;
        while ((start = value.IndexOf(part, start, StringComparison.Ordinal)) >= 0)
        {
            count++;
            start += part.Length;
        }

        return count;
    }
}
