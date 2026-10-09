using System.Diagnostics;
using System.Globalization;
using System.Text;
using Parrot.Agent;
using Parrot.Config;
using Parrot.Diagnostics;
using Parrot.Protocol;
using Parrot.Store;

namespace Parrot.Process;

// Each stdout line is an event delivered to the agent as it arrives, batched per
// poll. Unlike an ordinary process, a running monitor does not hold the turn open.
internal sealed class ShellProcessMonitor(
    IAgentSession agent,
    IPromptTemplateCatalog templates,
    ToolOutputBlobStore outputBlobs,
    IDiagnosticLog diagnostics,
    TimeSpan timeout) : IShellProcessReport
{
    private const int FloodLines = 100;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan FloodWindow = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan KillGrace = TimeSpan.FromSeconds(5);
    private static readonly ProcessSignal Terminate = new(15);
    private static readonly ProcessSignal Kill = new(9);
    private int _events;
    private string _reason = "exited";
    private string _exitCode = string.Empty;
    private string _error = string.Empty;

    public bool BlocksTurn => false;

    public ShellProcessActivityKind ActivityKind => ShellProcessActivityKind.Monitor;

    public async Task Observe(ActiveShellProcessState state, IProcessExecution execution, CancellationToken lifetime)
    {
        try
        {
            await Task.Yield();
            await Stream(state, execution, lifetime).ConfigureAwait(false);
            var result = await execution.Result.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            _exitCode = result.ExitCode.ToString(CultureInfo.InvariantCulture);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception failure)
        {
            _reason = "failed";
            _error = failure.Message;
            diagnostics.Write(new DiagnosticEvent("monitor", "observe", DiagnosticSeverity.Error)
            {
                AgentSessionId = state.OwnerAgentSessionId,
                CorrelationId = state.ProcessId,
                Outcome = "failed",
                ErrorCode = DiagnosticEvent.ClassifyFailure(failure),
            });
            Signal(execution, Kill);
        }
    }

    public string Complete(ActiveShellProcessState state, string output) =>
        templates.Render("monitor.ended", [
            new PromptTemplateArgument("name", state.Name),
            new PromptTemplateArgument("description", state.Description),
            new PromptTemplateArgument("events", _events.ToString(CultureInfo.InvariantCulture)),
            new PromptTemplateArgument("reason", _reason),
            new PromptTemplateArgument("exit_code", _exitCode),
            new PromptTemplateArgument("error", _error),
            new PromptTemplateArgument("timeout_ms", ((long)timeout.TotalMilliseconds).ToString(CultureInfo.InvariantCulture)),
        ]);

    private static (string Text, int Count) TakeLines(StringBuilder pending, bool all)
    {
        var text = pending.ToString();
        var end = all ? text.Length : text.LastIndexOf('\n') + 1;
        _ = pending.Remove(0, end);
        var lines = text[..end].TrimEnd('\n');
        return (lines, lines.Length == 0 ? 0 : lines.AsSpan().Count('\n') + 1);
    }

    private static void Signal(IProcessExecution execution, ProcessSignal signal)
    {
        try
        {
            execution.SendSignal(signal, CancellationToken.None);
        }
        catch (Exception failure) when (failure is InvalidOperationException or IOException or PlatformNotSupportedException)
        {
            // The process may already have exited; its completion still ends the stream.
        }
    }

    private async Task Stream(ActiveShellProcessState state, IProcessExecution execution, CancellationToken lifetime)
    {
        var path = execution.StdoutPath ?? throw new InvalidOperationException("A monitor requires pipe output.");
        using var reader = new StreamReader(
            new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete),
            Encoding.UTF8);
        var pending = new StringBuilder();
        var started = Stopwatch.GetTimestamp();
        var windowStarted = started;
        var windowLines = 0;
        long? stopped = null;
        var killed = false;

        while (true)
        {
            var completed = execution.Result.IsCompleted;
            _ = pending.Append(await reader.ReadToEndAsync(lifetime).ConfigureAwait(false));
            var (lines, count) = TakeLines(pending, completed);

            if (count > 0 && _reason != "flooded")
            {
                if (Stopwatch.GetElapsedTime(windowStarted) > FloodWindow)
                {
                    windowStarted = Stopwatch.GetTimestamp();
                    windowLines = 0;
                }

                windowLines += count;

                if (windowLines > FloodLines && stopped is null)
                {
                    stopped = Stop(execution, "flooded");
                }
                else
                {
                    await Send(state, lines, count, lifetime).ConfigureAwait(false);
                }
            }

            if (completed)
            {
                return;
            }

            if (stopped is null && Stopwatch.GetElapsedTime(started) >= timeout)
            {
                stopped = Stop(execution, "timed_out");
            }

            if (stopped is { } stoppedAt && !killed && Stopwatch.GetElapsedTime(stoppedAt) >= KillGrace)
            {
                Signal(execution, Kill);
                killed = true;
            }

            await Task.Delay(PollInterval, lifetime).ConfigureAwait(false);
        }
    }

    private long Stop(IProcessExecution execution, string reason)
    {
        _reason = reason;
        Signal(execution, Terminate);
        return Stopwatch.GetTimestamp();
    }

    private async Task Send(ActiveShellProcessState state, string lines, int count, CancellationToken lifetime)
    {
        var text = templates.Render("monitor.event", [
            new PromptTemplateArgument("name", state.Name),
            new PromptTemplateArgument("description", state.Description),
            new PromptTemplateArgument("lines", lines),
        ]);

        if (ToolOutputBlobStore.IsOversized(text))
        {
            text = await outputBlobs.Persist(text, lifetime).ConfigureAwait(false);
        }

        _ = await agent
            .Send(
                [ConversationPart.TextPart(text)],
                Identifier.MessageId(),
                Delivery.Steer,
                new IncomingActivity(state.Name, $"monitor {state.Name} event"),
                lifetime)
            .ConfigureAwait(false);
        _events += count;
    }
}
