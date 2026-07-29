using Parrot.Questions;

namespace Parrot.Core.Tests;

internal sealed class QuestionBrokerTests
{
    [Test]
    public async Task A_valid_reply_settles_the_waiter_and_is_removed(CancellationToken cancellationToken)
    {
        using var broker = new QuestionBroker();
        var asking = broker.Ask([Question("colour", false, false)], cancellationToken);
        var pending = await WaitForPending(broker, cancellationToken);

        broker.Reply(pending.Id, new QuestionReply([new QuestionAnswer("colour", ["blue"], string.Empty)]));

        var reply = await asking;
        _ = await Assert.That(reply.Answers.Single().OptionIds.Single()).IsEqualTo("blue");
        _ = await Assert.That(broker.Pending()).IsEmpty();
    }

    [Test]
    public async Task An_invalid_reply_leaves_the_request_pending(CancellationToken cancellationToken)
    {
        using var broker = new QuestionBroker();
        var asking = broker.Ask([Question("colour", false, false)], cancellationToken);
        var pending = await WaitForPending(broker, cancellationToken);

        _ = await Assert.That(() => broker.Reply(
            pending.Id,
            new QuestionReply([new QuestionAnswer("colour", ["red"], string.Empty)])))
            .Throws<QuestionException>();
        _ = await Assert.That(broker.Pending().Single().Id).IsEqualTo(pending.Id);

        broker.Reply(pending.Id, new QuestionReply([new QuestionAnswer("colour", ["blue"], string.Empty)]));
        _ = await asking;
    }

    [Test]
    public async Task Multiple_and_custom_answers_are_validated(CancellationToken cancellationToken)
    {
        using var broker = new QuestionBroker();
        var asking = broker.Ask([Question("colour", true, true)], cancellationToken);
        var pending = await WaitForPending(broker, cancellationToken);

        broker.Reply(pending.Id, new QuestionReply([new QuestionAnswer("colour", ["blue", "green"], "violet")]));

        var answer = (await asking).Answers.Single();
        _ = await Assert.That(answer.OptionIds).Count().IsEqualTo(2);
        _ = await Assert.That(answer.Custom).IsEqualTo("violet");
    }

    [Test]
    public async Task Cancellation_removes_an_unanswered_request(CancellationToken cancellationToken)
    {
        using var broker = new QuestionBroker();
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var asking = broker.Ask([Question("colour", false, false)], stopping.Token);
        _ = await WaitForPending(broker, cancellationToken);

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
    }

    private static QuestionDefinition Question(string id, bool multiple, bool custom) =>
        new(id, string.Empty, "Pick a colour", [new QuestionOption("blue", "Blue"), new QuestionOption("green", "Green")], multiple, custom);

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
