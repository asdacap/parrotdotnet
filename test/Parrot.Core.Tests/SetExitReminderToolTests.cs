using Parrot.Agent;
using Parrot.Llm;
using Parrot.Security;
using Parrot.Store;
using Parrot.Tools;

namespace Parrot.Core.Tests;

internal sealed class SetExitReminderToolTests
{
    [Test]
    public async Task Reminder_values_are_set_replaced_or_cleared()
    {
        using var database = SessionDatabase.Open(":memory:");
        var repository = new EventRepository(database);
        var reminder = new ExitReminder(repository, TestModels.PromptTemplates, "agent");
        var tool = new SetExitReminderTool(reminder, TestModels.PromptTemplates);
        var cases = new (string Json, string? Expected, string Output)[]
        {
            ("{\"reminder\":\"alpha\"}", "alpha", "Exit reminder set."),
            ("{\"reminder\":\"beta\"}", "beta", "Exit reminder set."),
            ("{\"reminder\":\"\"}", null, "Exit reminder cleared."),
            ("{}", null, "Exit reminder cleared."),
        };

        foreach (var testCase in cases)
        {
            var (json, expected, output) = testCase;
            var result = await tool.Execute(new ToolInvocation("call", json), Selection(), CancellationToken.None);
            _ = await Assert.That(result.Text).IsEqualTo(output);
            _ = await Assert.That(repository.LatestExitReminder("agent")).IsEqualTo(expected);
        }
    }

    [Test]
    public async Task Null_and_malformed_arguments_are_handled_strictly()
    {
        using var database = SessionDatabase.Open(":memory:");
        var repository = new EventRepository(database);
        var reminder = new ExitReminder(repository, TestModels.PromptTemplates, "agent");
        var tool = new SetExitReminderTool(reminder, TestModels.PromptTemplates);

        var nullResult = await tool.Execute(new ToolInvocation("null", "null"), Selection(), CancellationToken.None);
        var nonString = await tool.Execute(new ToolInvocation("number", "{\"reminder\":1}"), Selection(), CancellationToken.None);
        var unexpected = await tool.Execute(new ToolInvocation("extra", "{\"extra\":true}"), Selection(), CancellationToken.None);

        _ = await Assert.That(nullResult.Text).DoesNotStartWith("error:");
        _ = await Assert.That(nonString.Text).StartsWith("error:");
        _ = await Assert.That(unexpected.Text).StartsWith("error:");
    }

    private static AgentTurnSelection Selection()
    {
        var provider = new UnusedProvider();
        var model = new ProviderModel(provider, new LLMModel("model", provider.Id));
        return new AgentTurnSelection(new ModelSelector(model.Selector), TestModels.Resolve(model), TestModels.Profile(), SecurityProfile.Compose(readOnly: false, [], [], []));
    }
}
