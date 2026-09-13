using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Parrot.Agent;
using Parrot.Diagnostics;
using Parrot.Queues;

namespace Parrot.Tools;

internal sealed class QueueTakeTool(IAgentQueues queues, IDiagnosticLog diagnostics) : ITool
{
    public string Name => "queue_take";

    public async Task<ToolExecutionResult> Execute(ToolInvocation invocation, AgentTurnSelection selection, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var outcome = "failed";
        string? errorCode = null;
        diagnostics.Write(new DiagnosticEvent("queue", "wait_started", DiagnosticSeverity.Information)
        {
            AgentSessionId = queues.SessionId,
            CorrelationId = invocation.CallId,
        });
        try
        {
            var input = QueueToolExecution.Deserialize(invocation.ArgumentsJson, QueueToolJsonContext.Default.QueueTakeToolInput);
            var name = QueueToolExecution.RequireName(input.Name);
            var count = input.Count ?? 1;
            var direction = QueueToolExecution.ParseDirection(input.Direction);
            var yieldAfter = input.YieldAfterMilliseconds ?? 30_000;
            if (count <= 0)
            {
                throw new FormatException("Tool argument 'count' must be a positive integer.");
            }

            if (yieldAfter is < 0 or > long.MaxValue / TimeSpan.TicksPerMillisecond)
            {
                throw new FormatException("Tool argument 'yield_after_ms' must be a non-negative integer within range.");
            }

            var deadline = Environment.TickCount64 + yieldAfter;
            QueueInfo? empty = null;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var taken = queues.TryTake(name, count, direction);

                    if (taken.Acquired)
                    {
                        outcome = taken.Items.Count > 0 ? "delivered" : "closed";
                        return QueueToolExecution.Serialize(
                            taken.Info ?? throw new QueueException("queue: missing take information"),
                            taken.Items);
                    }
                }
                catch (QueueEmptyException failure)
                {
                    empty = failure.Info;
                }

                var remaining = deadline - Environment.TickCount64;

                if (remaining <= 0)
                {
                    outcome = "timeout";
                    return QueueToolExecution.Serialize(empty ?? queues.Get(name), []);
                }

                await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(10, remaining)), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception failure) when (failure is JsonException or FormatException or QueueException)
        {
            errorCode = DiagnosticEvent.ClassifyFailure(failure);
            return ToolResultFormatter.Error(invocation, failure.Message);
        }
        catch (Exception failure)
        {
            outcome = failure is OperationCanceledException ? "cancelled" : "failed";
            errorCode = DiagnosticEvent.ClassifyFailure(failure);
            throw;
        }
        finally
        {
            diagnostics.Write(new DiagnosticEvent("queue", "wait_completed", outcome == "failed" ? DiagnosticSeverity.Error : DiagnosticSeverity.Information)
            {
                AgentSessionId = queues.SessionId,
                CorrelationId = invocation.CallId,
                DurationMilliseconds = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                Outcome = outcome,
                ErrorCode = errorCode,
            });
        }
    }

    internal sealed class Input
    {
        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("count")]
        public int? Count { get; init; }

        [JsonPropertyName("direction")]
        public string? Direction { get; init; }

        [JsonPropertyName("yield_after_ms")]
        public long? YieldAfterMilliseconds { get; init; }
    }
}
