using Parrot.Agent;
using Parrot.Events;
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
        using var broker = new EventBroker();
        var reminder = new ExitReminder(repository, broker, TestModels.PromptTemplates, "agent");
        ITool tool = new SetExitReminderTool(reminder, TestModels.PromptTemplates);
        var provider = new UnusedProvider();
        var model = new ProviderModel(provider, new LLMModel("model", provider.Id));
        var selection = new AgentTurnSelection(new ModelSelector(model.Selector), TestModels.Resolve(model), new TestProfileFixture().Mode, SecurityProfile.Compose(readOnly: false, [], [], []));
        var cases = new (string Json, string? Expected, string Output)[]
        {
            ("{\"reminder\":\"alpha\"}", "alpha", "Exit reminder set: alpha"),
            ("{\"reminder\":\"beta\"}", "beta", "Exit reminder set: beta"),
            ("{\"reminder\":\"  first\\n{second}  \"}", "  first\n{second}  ", "Exit reminder set:   first\n{second}  "),
            ("{\"reminder\":\" \"}", " ", "Exit reminder set:  "),
            ("{\"reminder\":\"\"}", null, "Exit reminder cleared."),
            ("{\"reminder\":null}", null, "Exit reminder cleared."),
            ("{}", null, "Exit reminder cleared."),
            ("{\"reminder\":\"alpha\"}", "alpha", "Exit reminder set: alpha"),
            ("{\"reminder\":\"null\"}", null, "Exit reminder cleared."),
            ("{\"reminder\":\"beta\"}", "beta", "Exit reminder set: beta"),
            ("{\"reminder\":\"undefined\"}", null, "Exit reminder cleared."),
            ("{\"reminder\":\"check null or undefined\"}", "check null or undefined", "Exit reminder set: check null or undefined"),
        };

        foreach (var testCase in cases)
        {
            var (json, expected, output) = testCase;
            var result = await tool.Execute(new ToolInvocation("call", json), selection, CancellationToken.None);
            _ = await Assert.That(result.Text).IsEqualTo(output);
            _ = await Assert.That(repository.LatestExitReminder("agent")).IsEqualTo(expected);
        }
    }

    [Test]
    public async Task Repeated_injections_count_up_and_reset_on_change_or_clear()
    {
        using var database = SessionDatabase.Open(":memory:");
        var repository = new EventRepository(database);
        using var broker = new EventBroker();
        var reminder = new ExitReminder(repository, broker, TestModels.PromptTemplates, "agent");
        var steps = new (string? Set, bool Clear, string? ExpectedBuild)[]
        {
            (null, false, null),
            ("X", false, "An exit reminder was set: X"),
            (null, false, "This is the 2nd exit reminder: X"),
            (null, false, "This is the 3rd exit reminder: X"),
            ("Y", false, "An exit reminder was set: Y"),
            (null, false, "This is the 2nd exit reminder: Y"),
            (null, true, null),
            ("Z", false, "An exit reminder was set: Z"),
            ("null", false, null),
            ("Z", false, "An exit reminder was set: Z"),
            ("undefined", false, null),
            ("Z", false, "An exit reminder was set: Z"),
        };

        foreach (var (set, clear, expectedBuild) in steps)
        {
            if (set is not null)
            {
                await reminder.Set(set, CancellationToken.None);
            }
            else if (clear)
            {
                await reminder.Set(null, CancellationToken.None);
            }

            _ = await Assert.That(reminder.Build()).IsEqualTo(expectedBuild);
        }
    }

    [Test]
    public async Task Null_and_malformed_arguments_are_handled_strictly()
    {
        using var database = SessionDatabase.Open(":memory:");
        var repository = new EventRepository(database);
        using var broker = new EventBroker();
        var reminder = new ExitReminder(repository, broker, TestModels.PromptTemplates, "agent");
        ITool tool = new SetExitReminderTool(reminder, TestModels.PromptTemplates);
        var provider = new UnusedProvider();
        var model = new ProviderModel(provider, new LLMModel("model", provider.Id));
        var selection = new AgentTurnSelection(new ModelSelector(model.Selector), TestModels.Resolve(model), new TestProfileFixture().Mode, SecurityProfile.Compose(readOnly: false, [], [], []));

        var nullResult = await tool.Execute(new ToolInvocation("null", "null"), selection, CancellationToken.None);
        var nonString = await tool.Execute(new ToolInvocation("number", "{\"reminder\":1}"), selection, CancellationToken.None);
        var unexpected = await tool.Execute(new ToolInvocation("extra", "{\"extra\":true}"), selection, CancellationToken.None);

        _ = await Assert.That(nullResult.Text).DoesNotStartWith("error:");
        _ = await Assert.That(nonString.Text).StartsWith("error:");
        _ = await Assert.That(unexpected.Text).StartsWith("error:");
    }
}
