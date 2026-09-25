using Google.Protobuf;
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
        IExitReminder exitReminder = new ExitReminder(repository, broker, TestModels.PromptTemplates, "agent");
        await exitReminder.Set("tests", "run the tests", cancellationToken);
        using var listener = broker.Subscribe();

        await exitReminder.Set("port", "finish the port", cancellationToken);
        if (clear)
        {
            _ = await listener.Reader.ReadAsync(cancellationToken);
            _ = await Assert.That(await exitReminder.Clear("port", cancellationToken)).IsTrue();
        }

        var announced = await listener.Reader.ReadAsync(cancellationToken);
        _ = await Assert.That(announced.PayloadCase).IsEqualTo(Event.PayloadOneofCase.ExitReminderChanged);
        _ = await Assert.That(announced.ExitReminderChanged.StateCase).IsEqualTo(expected);
        _ = await Assert.That(announced.ExitReminderChanged.Title).IsEqualTo("port");
        _ = await Assert.That(announced.AgentSessionId).IsEqualTo("agent");
        _ = await Assert.That(repository.ExitReminders("agent").Count).IsEqualTo(expectedRestored);
        _ = await Assert.That(new ExitReminder(repository, broker, TestModels.PromptTemplates, "agent").Titles.Count)
            .IsEqualTo(expectedRestored);
    }

    [Test]
    public async Task Untitled_legacy_events_restore_under_the_reminder_title()
    {
        using var database = SessionDatabase.Open(":memory:");
        var repository = new EventRepository(database);
        repository.AppendExitReminderChanged(new Event { Id = "goal", AgentSessionId = "agent" }, "goal", "reach the goal");
        using (var legacy = database.Connection.CreateCommand())
        {
            legacy.CommandText =
                "INSERT INTO event (id, agent_session, payload, created_at) VALUES ('legacy', 'agent', $payload, 0);";
            _ = legacy.Parameters.AddWithValue(
                "$payload",
                new Event
                {
                    Id = "legacy",
                    AgentSessionId = "agent",
                    ExitReminderChanged = new ExitReminderChanged { Description = "finish the port" },
                }.ToByteArray());
            _ = await legacy.ExecuteNonQueryAsync();
        }

        _ = await Assert.That(repository.ExitReminders("agent")).IsEquivalentTo(
            [new ExitReminderEntry("goal", "reach the goal"), new ExitReminderEntry("reminder", "finish the port")]);
    }
}
