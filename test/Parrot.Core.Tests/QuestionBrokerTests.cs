using Parrot.Questions;

namespace Parrot.Core.Tests;

internal sealed class QuestionBrokerTests
{
    [Test]
    public async Task A_valid_reply_settles_the_waiter_and_is_removed(CancellationToken cancellationToken)
    {
        using var broker = new QuestionBroker(Timeout.InfiniteTimeSpan, TimeProvider.System, TestDiagnosticLog.Instance);
        var asking = broker.Ask([new QuestionDefinition("colour", "Pick a colour", ["Blue", "Green"], false, false)], cancellationToken);
        var pending = await WaitForPending(broker, cancellationToken);

        broker.Reply(pending.Id, new QuestionReply([new QuestionAnswer("blue")]));

        var reply = await asking;
        _ = await Assert.That(reply.Answers.Single().Text).IsEqualTo("blue");
        _ = await Assert.That(broker.Pending()).IsEmpty();
    }

    [Test]
    public async Task An_invalid_reply_leaves_the_request_pending(CancellationToken cancellationToken)
    {
        using var broker = new QuestionBroker(Timeout.InfiniteTimeSpan, TimeProvider.System, TestDiagnosticLog.Instance);
        var asking = broker.Ask([new QuestionDefinition("colour", "Pick a colour", ["Blue", "Green"], false, false)], cancellationToken);
        var pending = await WaitForPending(broker, cancellationToken);

        _ = await Assert.That(() => broker.Reply(
            pending.Id,
            new QuestionReply([new QuestionAnswer(string.Empty)])))
            .Throws<QuestionException>();
        _ = await Assert.That(broker.Pending().Single().Id).IsEqualTo(pending.Id);

        broker.Reply(pending.Id, new QuestionReply([new QuestionAnswer("blue")]));
        _ = await asking;
    }

    [Test]
    public async Task Multiple_and_custom_answers_are_validated(CancellationToken cancellationToken)
    {
        using var broker = new QuestionBroker(Timeout.InfiniteTimeSpan, TimeProvider.System, TestDiagnosticLog.Instance);
        var asking = broker.Ask([new QuestionDefinition("colour", "Pick a colour", ["Blue", "Green"], true, true)], cancellationToken);
        var pending = await WaitForPending(broker, cancellationToken);

        broker.Reply(pending.Id, new QuestionReply([new QuestionAnswer("blue, green, violet")]));

        var answer = (await asking).Answers.Single();
        _ = await Assert.That(answer.Text).IsEqualTo("blue, green, violet");
    }

    [Test]
    public async Task Timeout_returns_user_away_removes_request_and_rejects_late_reply(CancellationToken cancellationToken)
    {
        var time = new ControlledTimeProvider();
        using var broker = new QuestionBroker(TimeSpan.FromMinutes(20), time, TestDiagnosticLog.Instance);
        var asking = broker.Ask([new QuestionDefinition("colour", "Pick a colour", ["Blue", "Green"], false, false)], cancellationToken);
        var pending = await WaitForPending(broker, cancellationToken);
        await time.WaitForTimer(cancellationToken);

        time.Advance(TimeSpan.FromMinutes(20));

        _ = await Assert.That((await asking).Kind).IsEqualTo(QuestionReplyKind.UserAway);
        _ = await Assert.That(broker.Pending()).IsEmpty();
        _ = await Assert.That(() => broker.Reply(
            pending.Id,
            new QuestionReply([new QuestionAnswer("blue")])))
            .Throws<QuestionException>();
    }

    [Test]
    public async Task Infinite_timeout_stays_pending_until_replied(CancellationToken cancellationToken)
    {
        var time = new ControlledTimeProvider();
        using var broker = new QuestionBroker(Timeout.InfiniteTimeSpan, time, TestDiagnosticLog.Instance);
        var asking = broker.Ask([new QuestionDefinition("colour", "Pick a colour", ["Blue", "Green"], false, false)], cancellationToken);
        var pending = await WaitForPending(broker, cancellationToken);

        time.Advance(TimeSpan.FromDays(1));
        _ = await Assert.That(asking.IsCompleted).IsFalse();
        broker.Reply(pending.Id, new QuestionReply([new QuestionAnswer("blue")]));

        _ = await Assert.That((await asking).Kind).IsEqualTo(QuestionReplyKind.Answered);
    }

    [Test]
    public async Task Reply_before_timeout_wins_and_timeout_does_not_replace_it(CancellationToken cancellationToken)
    {
        var time = new ControlledTimeProvider();
        using var broker = new QuestionBroker(TimeSpan.FromMinutes(20), time, TestDiagnosticLog.Instance);
        var asking = broker.Ask([new QuestionDefinition("colour", "Pick a colour", ["Blue", "Green"], false, false)], cancellationToken);
        var pending = await WaitForPending(broker, cancellationToken);
        await time.WaitForTimer(cancellationToken);

        broker.Reply(pending.Id, new QuestionReply([new QuestionAnswer("blue")]));
        time.Advance(TimeSpan.FromMinutes(20));

        var reply = await asking;
        _ = await Assert.That(reply.Kind).IsEqualTo(QuestionReplyKind.Answered);
        _ = await Assert.That(reply.Answers.Single().Text).IsEqualTo("blue");
    }

    [Test]
    public async Task Reply_before_cancellation_wins_and_cancellation_does_not_replace_it(CancellationToken cancellationToken)
    {
        using var broker = new QuestionBroker(Timeout.InfiniteTimeSpan, TimeProvider.System, TestDiagnosticLog.Instance);
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var asking = broker.Ask([new QuestionDefinition("colour", "Pick a colour", ["Blue", "Green"], false, false)], stopping.Token);
        var pending = await WaitForPending(broker, cancellationToken);

        broker.Reply(pending.Id, new QuestionReply([new QuestionAnswer("blue")]));
        await stopping.CancelAsync();

        _ = await Assert.That((await asking).Kind).IsEqualTo(QuestionReplyKind.Answered);
    }

    [Test]
    public async Task Cancellation_removes_an_unanswered_request(CancellationToken cancellationToken)
    {
        using var broker = new QuestionBroker(Timeout.InfiniteTimeSpan, TimeProvider.System, TestDiagnosticLog.Instance);
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var asking = broker.Ask([new QuestionDefinition("colour", "Pick a colour", ["Blue", "Green"], false, false)], stopping.Token);
        var pending = await WaitForPending(broker, cancellationToken);

        await stopping.CancelAsync();
        try
        {
            _ = await asking.ConfigureAwait(false);
            Assert.Fail("the cancelled question should not complete successfully");
        }
        catch (OperationCanceledException)
        {
        }

        _ = await Assert.That(broker.Pending()).IsEmpty();
        _ = await Assert.That(() => broker.Reply(
            pending.Id,
            new QuestionReply([new QuestionAnswer("blue")])))
            .Throws<QuestionException>();
    }

    [Test]
    public async Task Rejection_removes_request_and_faults_waiter(CancellationToken cancellationToken)
    {
        using var broker = new QuestionBroker(Timeout.InfiniteTimeSpan, TimeProvider.System, TestDiagnosticLog.Instance);
        var asking = broker.Ask([new QuestionDefinition("colour", "Pick a colour", ["Blue", "Green"], false, false)], cancellationToken);
        var pending = await WaitForPending(broker, cancellationToken);

        broker.Reject(pending.Id);

        _ = await Assert.That(asking).Throws<QuestionRejectedException>();
        _ = await Assert.That(broker.Pending()).IsEmpty();
        _ = await Assert.That(() => broker.Reject(pending.Id)).Throws<QuestionException>();
    }

    [Test]
    public async Task Disposal_rejects_pending_and_future_requests(CancellationToken cancellationToken)
    {
        var broker = new QuestionBroker(Timeout.InfiniteTimeSpan, TimeProvider.System, TestDiagnosticLog.Instance);
        var asking = broker.Ask([new QuestionDefinition("colour", "Pick a colour", ["Blue", "Green"], false, false)], cancellationToken);
        _ = await WaitForPending(broker, cancellationToken);

        broker.Dispose();
        broker.Dispose();

        _ = await Assert.That(asking).Throws<QuestionRejectedException>();
        _ = await Assert.That(broker.Pending()).IsEmpty();
        _ = await Assert.That(async () => await broker.Ask([new QuestionDefinition("colour", "Pick a colour", ["Blue", "Green"], false, false)], cancellationToken))
            .Throws<ObjectDisposedException>();
    }

    [Test]
    [Arguments(1000, 1, 1000L)]
    [Arguments(1000, 2500001, 750L)]
    [Arguments(1000, 10000000, 0L)]
    [Arguments(1000, 11000000, 0L)]
    [Arguments(-1, 11000000, null)]
    public async Task Pending_timing_preserves_the_original_deadline(
        int timeoutMilliseconds, long elapsedTicks, long? expectedRemaining, CancellationToken cancellationToken)
    {
        var time = new ControlledTimeProvider();
        using var broker = new QuestionBroker(TimeSpan.FromMilliseconds(timeoutMilliseconds), time, TestDiagnosticLog.Instance);
        var asking = broker.Ask([new QuestionDefinition("colour", "Pick a colour", ["Blue"], false, false)], cancellationToken);
        var pending = await WaitForPending(broker, cancellationToken);
        _ = await Assert.That(pending.RemainingTimeoutMilliseconds)
            .IsEqualTo(timeoutMilliseconds == -1 ? null : (long?)timeoutMilliseconds);

        time.AdvanceClock(TimeSpan.FromTicks(elapsedTicks));
        _ = await Assert.That(broker.Pending().Single().RemainingTimeoutMilliseconds).IsEqualTo(expectedRemaining);
        _ = await Assert.That(broker.Pending().Single().RemainingTimeoutMilliseconds).IsEqualTo(expectedRemaining);
        _ = await Assert.That(asking.IsCompleted).IsFalse();

        if (timeoutMilliseconds == -1)
        {
            broker.Reply(pending.Id, new QuestionReply([new QuestionAnswer("Blue")]));
            _ = await Assert.That((await asking).Kind).IsEqualTo(QuestionReplyKind.Answered);
        }
        else
        {
            time.Advance(TimeSpan.FromTicks(Math.Max(0, (timeoutMilliseconds * TimeSpan.TicksPerMillisecond) - elapsedTicks)));
            _ = await Assert.That((await asking).Kind).IsEqualTo(QuestionReplyKind.UserAway);
        }

        _ = await Assert.That(broker.Pending()).IsEmpty();
    }

    private static async Task<PendingQuestionRequest> WaitForPending(
        QuestionBroker broker,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var pending = broker.Pending();
            if (pending.Count == 1)
            {
                return pending[0];
            }

            await Task.Delay(1, cancellationToken).ConfigureAwait(false);
        }
    }
}
