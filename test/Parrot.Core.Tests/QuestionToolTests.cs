using Parrot.Questions;
using Parrot.Tools;

namespace Parrot.Core.Tests;

internal sealed class QuestionToolTests
{
    [Test]
    public async Task Schema_shaped_input_is_mapped_and_answer_uses_labels(CancellationToken cancellationToken)
    {
        using var broker = new QuestionBroker(Timeout.InfiniteTimeSpan, TimeProvider.System);
        var tool = new QuestionTool(broker);
        const string argumentsJson =
            """
            {"questions":[{"id":"colour","header":"Palette","prompt":"Pick a colour","options":[{"id":"blue","label":"Blue"}],"multiple":true,"custom":true}]}
            """;
        var executing = tool.Execute(
            new ToolInvocation("test-call", argumentsJson),
            cancellationToken);
        var pending = await WaitForPending(broker, cancellationToken);
        var question = pending.Questions.Single();

        _ = await Assert.That(question.Id).IsEqualTo("colour");
        _ = await Assert.That(question.Header).IsEqualTo("Palette");
        _ = await Assert.That(question.Prompt).IsEqualTo("Pick a colour");
        _ = await Assert.That(question.Options.Single().Id).IsEqualTo("blue");
        _ = await Assert.That(question.Options.Single().Label).IsEqualTo("Blue");
        _ = await Assert.That(question.Multiple).IsTrue();
        _ = await Assert.That(question.Custom).IsTrue();

        broker.Reply(pending.Id, new QuestionReply([new QuestionAnswer("colour", ["blue"], string.Empty)]));

        _ = await Assert.That((await executing).Text).IsEqualTo("Question: Pick a colour\nAnswer: Blue");
    }

    [Test]
    public async Task Answers_follow_question_and_selection_order_with_custom_last(CancellationToken cancellationToken)
    {
        using var broker = new QuestionBroker(Timeout.InfiniteTimeSpan, TimeProvider.System);
        const string argumentsJson =
            """
            {"questions":[{"id":"colour","prompt":"Pick colours","options":[{"id":"red","label":"Red"},{"id":"blue","label":"Blue"}],"multiple":true,"custom":true},{"id":"size","prompt":"Pick a size","options":[{"id":"large","label":"Large"}]}]}
            """;
        var executing = new QuestionTool(broker).Execute(
            new ToolInvocation("test-call", argumentsJson),
            cancellationToken);
        var pending = await WaitForPending(broker, cancellationToken);

        broker.Reply(pending.Id, new QuestionReply(
        [
            new QuestionAnswer("size", ["large"], string.Empty),
            new QuestionAnswer("colour", ["blue", "red"], "Green"),
        ]));

        _ = await Assert.That((await executing).Text).IsEqualTo(
            "Question: Pick colours\nAnswer: Blue, Red, Green\n\nQuestion: Pick a size\nAnswer: Large");
    }

    [Test]
    [Arguments("{\"questions\":[],\"unexpected\":true}")]
    [Arguments("{\"questions\":[{\"id\":\"colour\",\"prompt\":\"Pick\",\"options\":[{\"id\":\"blue\",\"label\":\"Blue\"}],\"unexpected\":true}]}")]
    [Arguments("{\"questions\":[{\"id\":\"colour\",\"prompt\":\"Pick\",\"options\":[{\"id\":\"blue\",\"label\":\"Blue\",\"unexpected\":true}]}]}")]
    public async Task Unknown_wire_properties_are_rejected(string argumentsJson, CancellationToken cancellationToken)
    {
        using var broker = new QuestionBroker(Timeout.InfiniteTimeSpan, TimeProvider.System);
        var result = (await new QuestionTool(broker).Execute(new ToolInvocation("test-call", argumentsJson), cancellationToken)).Text;

        _ = await Assert.That(result).StartsWith("error:");
        _ = await Assert.That(broker.Pending()).IsEmpty();
    }

    [Test]
    public async Task Timeout_returns_user_away_as_a_normal_result(CancellationToken cancellationToken)
    {
        var time = new ControlledTimeProvider();
        using var broker = new QuestionBroker(TimeSpan.FromMinutes(20), time);
        var executing = new QuestionTool(broker).Execute(
            new ToolInvocation(
                "test-call",
                """{"questions":[{"id":"colour","prompt":"Pick","options":[{"id":"blue","label":"Blue"}]}]}"""),
            cancellationToken);
        _ = await WaitForPending(broker, cancellationToken);
        await time.WaitForTimer(cancellationToken);

        time.Advance(TimeSpan.FromMinutes(20));

        _ = await Assert.That((await executing).Text).IsEqualTo("The user is away.");
    }

    [Test]
    public async Task Rejected_question_returns_error(CancellationToken cancellationToken)
    {
        using var broker = new QuestionBroker(Timeout.InfiniteTimeSpan, TimeProvider.System);
        var executing = new QuestionTool(broker).Execute(
            new ToolInvocation(
                "test-call",
                """{"questions":[{"id":"colour","prompt":"Pick","options":[{"id":"blue","label":"Blue"}]}]}"""),
            cancellationToken);
        var pending = await WaitForPending(broker, cancellationToken);

        broker.Reject(pending.Id);

        _ = await Assert.That((await executing).Text).IsEqualTo("error: question request rejected");
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
