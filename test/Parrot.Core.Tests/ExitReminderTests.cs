using Parrot.Agent;
using Parrot.Events;
using Parrot.Protocol;
using Parrot.Store;

namespace Parrot.Core.Tests;

internal sealed class ExitReminderTests
{
    [Test]
    [Arguments(false, ExitReminderChanged.StateOneofCase.Description, 2)]
    [Arguments(true, ExitReminderChanged.StateOneofCase.Cleared, 1)]
    public async Task Set_and_clear_record_and_announce_the_change(
        bool clear,
        ExitReminderChanged.StateOneofCase expected,
        int expectedRestored,
        CancellationToken cancellationToken)
    {
        using var database = SessionDatabase.Open(":memory:");
        var repository = new EventRepository(database);
        using var broker = new EventBroker();
        IExitReminder exitReminder = new ExitReminder(TestModels.History(repository, "agent"), broker, TestModels.PromptTemplates, "agent");
        await exitReminder.Set("tests", "run the tests", cancellationToken);
        using var listener = broker.Subscribe();

        await exitReminder.Set("port", "finish the port", cancellationToken);
        if (clear)
        {
            _ = await listener.Reader.ReadAsync(cancellationToken);
            _ = await Assert.That(await exitReminder.Clear("port", cancellationToken)).IsTrue();
        }

        var announced = await listener.Reader.ReadAsync(cancellationToken);
        var changed = ExitReminderChanged.Parser.ParseFrom(announced.AgentNotice.Data);
        _ = await Assert.That(announced.AgentNotice.Kind).IsEqualTo("exit_reminder_changed");
        _ = await Assert.That(announced.AgentNotice.Notice)
            .IsEqualTo(clear ? "Exit reminder cleared: port" : "Exit reminder set: port: finish the port");
        _ = await Assert.That(changed.StateCase).IsEqualTo(expected);
        _ = await Assert.That(changed.Title).IsEqualTo("port");
        _ = await Assert.That(announced.AgentSessionId).IsEqualTo("agent");
        _ = await Assert.That(new ExitReminder(TestModels.History(repository, "agent"), broker, TestModels.PromptTemplates, "agent").Titles.Count)
            .IsEqualTo(expectedRestored);
    }

    [Test]
    public async Task Notices_lists_recorded_notices_of_one_kind_in_order()
    {
        using var database = SessionDatabase.Open(":memory:");
        var repository = new EventRepository(database);
        foreach (var (id, kind) in new[] { ("first", "wanted"), ("other", "unwanted"), ("second", "wanted") })
        {
            _ = repository.Append(
                new Event { Id = id, AgentSessionId = "agent", AgentNotice = new AgentNotice { Kind = kind, Notice = id } },
                null,
                null);
        }

        _ = await Assert.That(string.Join(',', repository.Notices("agent", "wanted").Select(notice => notice.Notice)))
            .IsEqualTo("first,second");
    }
}
