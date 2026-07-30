using Parrot.Questions;
using Parrot.Tools;

namespace Parrot.Core.Tests;

internal sealed class QuestionToolTests
{
    [Test]
    public async Task Schema_shaped_input_is_mapped_and_result_uses_wire_names(CancellationToken cancellationToken)
    {
        using var broker = new QuestionBroker();
        var tool = new QuestionTool(broker);
        var executing = tool.Execute(
            """
            {"questions":[{"id":"colour","header":"Palette","prompt":"Pick a colour","options":[{"id":"blue","label":"Blue"}],"multiple":true,"custom":true}]}
            """,
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

        _ = await Assert.That(await executing).IsEqualTo(
            """{"answers":[{"question_id":"colour","option_ids":["blue"],"custom":""}]}""");
    }

    [Test]
    [Arguments("{\"questions\":[],\"unexpected\":true}")]
    [Arguments("{\"questions\":[{\"id\":\"colour\",\"prompt\":\"Pick\",\"options\":[{\"id\":\"blue\",\"label\":\"Blue\"}],\"unexpected\":true}]}")]
    [Arguments("{\"questions\":[{\"id\":\"colour\",\"prompt\":\"Pick\",\"options\":[{\"id\":\"blue\",\"label\":\"Blue\",\"unexpected\":true}]}]}")]
    public async Task Unknown_wire_properties_are_rejected(string argumentsJson, CancellationToken cancellationToken)
    {
        using var broker = new QuestionBroker();
        var result = await new QuestionTool(broker).Execute(argumentsJson, cancellationToken);

        _ = await Assert.That(result).StartsWith("error:");
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
