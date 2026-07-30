using Parrot.Protocol;
using GeneratedParrot = Parrot.Protocol.Parrot;

namespace Parrot.Cli.Tests;

internal sealed class ScriptedInvokerIsolationTests
{
    [Test]
    public async Task Pending_questions_are_scoped_by_user_session()
    {
        var invoker = new ScriptedInvoker();
        var client = new GeneratedParrot.ParrotClient(invoker);
        invoker.AddPendingQuestion("first", new PendingQuestion { Id = "shared" });
        invoker.AddPendingQuestion("second", new PendingQuestion { Id = "shared" });

        _ = await client.ReplyQuestionAsync(new ReplyQuestionRequest
        {
            UserSessionId = "first",
            QuestionRequestId = "shared",
        });

        var first = await client.ListPendingQuestionsAsync(new ListPendingQuestionsRequest { UserSessionId = "first" });
        var second = await client.ListPendingQuestionsAsync(new ListPendingQuestionsRequest { UserSessionId = "second" });

        _ = await Assert.That(first.Questions).IsEmpty();
        _ = await Assert.That(second.Questions).HasSingleItem();
        _ = await Assert.That(second.Questions[0].Id).IsEqualTo("shared");
    }

    [Test]
    public async Task Published_events_are_scoped_by_user_session(CancellationToken cancellationToken)
    {
        var invoker = new ScriptedInvoker();
        var client = new GeneratedParrot.ParrotClient(invoker);
        using var first = client.Listen(new ListenRequest { UserSessionId = "first" }, cancellationToken: cancellationToken);
        using var second = client.Listen(new ListenRequest { UserSessionId = "second" }, cancellationToken: cancellationToken);

        await invoker.Publish("first", new Event { Id = "first-event" });

        _ = await Assert.That(await first.ResponseStream.MoveNext(cancellationToken)).IsTrue();
        _ = await Assert.That(first.ResponseStream.Current.Id).IsEqualTo("first-event");
        _ = await Assert.That(second.ResponseStream.MoveNext(cancellationToken).IsCompleted).IsFalse();
    }
}
