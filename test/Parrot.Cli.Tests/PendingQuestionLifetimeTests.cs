using System.Threading.Channels;
using Parrot.Cli.Enhanced;
using Parrot.Protocol;
using GeneratedParrot = Parrot.Protocol.Parrot;

namespace Parrot.Cli.Tests;

internal sealed class PendingQuestionLifetimeTests
{
    [Test]
    public async Task Countdown_extrapolates_during_rpc_failure_and_server_closure_remains_authoritative(
        CancellationToken cancellationToken)
    {
        var invoker = new ScriptedInvoker();
        invoker.AddPendingQuestion(new PendingQuestion { Id = "question", RemainingTimeoutMs = 1500 });
        var time = new CountdownTimeProvider();
        var updates = Channel.CreateUnbounded<long?>();
        using var lifetime = new PendingQuestionLifetime(
            new GeneratedParrot.ParrotClient(invoker),
            "session-1",
            "question",
            TimeSpan.FromMilliseconds(1),
            time,
            (seconds, token) => updates.Writer.WriteAsync(seconds, token).AsTask());
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var running = lifetime.Run(stopping.Token);
        await lifetime.WaitUntilReady(cancellationToken);
        _ = await Assert.That(await updates.Reader.ReadAsync(cancellationToken)).IsEqualTo(2);
        invoker.FailQuestionListing = true;
        time.Advance(TimeSpan.FromSeconds(1));
        _ = await Assert.That(await updates.Reader.ReadAsync(cancellationToken)).IsEqualTo(1);
        time.Advance(TimeSpan.FromSeconds(1));
        _ = await Assert.That(await updates.Reader.ReadAsync(cancellationToken)).IsEqualTo(0);
        _ = await Assert.That(lifetime.IsClosed).IsFalse();
        _ = await Assert.That(invoker.QuestionReplies).IsEmpty();
        _ = await Assert.That(invoker.QuestionRejections).IsEmpty();

        invoker.UpdateQuestionTimeout("question", null);
        invoker.FailQuestionListing = false;
        _ = await Assert.That(await updates.Reader.ReadAsync(cancellationToken)).IsNull();
        invoker.RemovePendingQuestion("question");
        await running.WaitAsync(cancellationToken);
        _ = await Assert.That(lifetime.IsClosed).IsTrue();
        _ = await Assert.That(updates.Reader.TryRead(out _)).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Fresh_reconciliation_handles_unknown_or_already_closed_requests(
        bool present, CancellationToken cancellationToken)
    {
        var invoker = new ScriptedInvoker();
        if (present)
        {
            invoker.AddPendingQuestion(new PendingQuestion { Id = "question" });
        }

        var updates = new List<long?>();
        using var lifetime = new PendingQuestionLifetime(
            new GeneratedParrot.ParrotClient(invoker),
            "session-1",
            "question",
            TimeSpan.FromMilliseconds(1),
            TimeProvider.System,
            (seconds, _) =>
            {
                updates.Add(seconds);
                return Task.CompletedTask;
            });
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var running = lifetime.Run(stopping.Token);
        await lifetime.WaitUntilReady(cancellationToken);
        _ = await Assert.That(lifetime.IsClosed).IsEqualTo(!present);
        await stopping.CancelAsync();
        await running;
        _ = await Assert.That(updates).IsEmpty();
    }

    private sealed class CountdownTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Read(ref _timestamp);

        public void Advance(TimeSpan elapsed) => Interlocked.Add(ref _timestamp, elapsed.Ticks);
    }
}
