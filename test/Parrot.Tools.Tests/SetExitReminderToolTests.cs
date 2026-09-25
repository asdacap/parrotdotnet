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
    public async Task Reminders_are_set_replaced_cleared_and_block_exit_until_all_are_cleared()
    {
        using var database = SessionDatabase.Open(":memory:");
        var repository = new EventRepository(database);
        using var broker = new EventBroker();
        var reminder = new ExitReminder(repository, broker, TestModels.PromptTemplates, "agent");
        ITool set = new SetExitReminderTool(reminder, TestModels.PromptTemplates);
        ITool clear = new ClearExitReminderTool(reminder, TestModels.PromptTemplates);
        var provider = new UnusedProvider();
        var model = new ProviderModel(provider, new LLMModel("model", provider.Id));
        var selection = new AgentTurnSelection(new ModelSelector(model.Selector), TestModels.Resolve(model), new TestProfileFixture().Mode, SecurityProfile.Compose(readOnly: false, [], [], []));
        const string First = "Exit reminders are set. You cannot finish until every one is cleared with clear_exit_reminder:\n";
        var steps = new (ITool? Tool, string? Json, string? Output, string? ExpectedBuild)[]
        {
            (null, null, null, null),
            (set, "{\"title\":\"tests\",\"description\":\"run tests\"}", "Exit reminder set: tests: run tests", First + "- tests: run tests"),
            (null, null, null, "This is the 2nd exit reminder. Remaining exit reminders:\n- tests: run tests"),
            (set, "{\"title\":\"docs\",\"description\":\"  update {docs}  \"}", "Exit reminder set: docs:   update {docs}  ", First + "- tests: run tests\n- docs:   update {docs}  "),
            (set, "{\"title\":\"tests\",\"description\":\"run all tests\"}", "Exit reminder set: tests: run all tests", First + "- tests: run all tests\n- docs:   update {docs}  "),
            (null, null, null, "This is the 2nd exit reminder. Remaining exit reminders:\n- tests: run all tests\n- docs:   update {docs}  "),
            (clear, "{\"title\":\"missing\"}", "error: No exit reminder titled missing. Active titles: tests, docs", "This is the 3rd exit reminder. Remaining exit reminders:\n- tests: run all tests\n- docs:   update {docs}  "),
            (clear, "{\"title\":\"tests\"}", "Exit reminder cleared: tests", First + "- docs:   update {docs}  "),
            (clear, "{\"title\":\"docs\"}", "Exit reminder cleared: docs", null),
            (clear, "{\"title\":\"docs\"}", "error: No exit reminder titled docs. Active titles: ", null),
        };

        foreach (var (tool, json, output, expectedBuild) in steps)
        {
            if (tool is not null && json is not null)
            {
                var result = await tool.Execute(new ToolInvocation("call", json), selection, CancellationToken.None);
                _ = await Assert.That(result.Text).IsEqualTo(output);
            }

            _ = await Assert.That(reminder.Build()).IsEqualTo(expectedBuild);
        }

        _ = await Assert.That(repository.ExitReminders("agent")).IsEmpty();
    }

    [Test]
    public async Task Null_and_malformed_arguments_are_handled_strictly()
    {
        using var database = SessionDatabase.Open(":memory:");
        var repository = new EventRepository(database);
        using var broker = new EventBroker();
        var reminder = new ExitReminder(repository, broker, TestModels.PromptTemplates, "agent");
        ITool set = new SetExitReminderTool(reminder, TestModels.PromptTemplates);
        ITool clear = new ClearExitReminderTool(reminder, TestModels.PromptTemplates);
        var provider = new UnusedProvider();
        var model = new ProviderModel(provider, new LLMModel("model", provider.Id));
        var selection = new AgentTurnSelection(new ModelSelector(model.Selector), TestModels.Resolve(model), new TestProfileFixture().Mode, SecurityProfile.Compose(readOnly: false, [], [], []));
        var cases = new (ITool Tool, string Json)[]
        {
            (set, "null"),
            (set, "{}"),
            (set, "{\"title\":\"tests\"}"),
            (set, "{\"title\":\"\",\"description\":\"run tests\"}"),
            (set, "{\"title\":\"tests\",\"description\":1}"),
            (set, "{\"title\":\"tests\",\"description\":\"run tests\",\"extra\":true}"),
            (clear, "null"),
            (clear, "{\"title\":\"\"}"),
            (clear, "{\"title\":1}"),
            (clear, "{\"title\":\"tests\",\"extra\":true}"),
        };

        foreach (var (tool, json) in cases)
        {
            var result = await tool.Execute(new ToolInvocation("call", json), selection, CancellationToken.None);
            _ = await Assert.That(result.Text).StartsWith("error:");
        }

        _ = await Assert.That(reminder.Build()).IsNull();
    }
}
