using Parrot.Agent;
using Parrot.Events;
using Parrot.Protocol;
using Parrot.Store;

namespace Parrot.Core.Tests;

internal sealed class ExitReminderTests
{
    [Test]
    [Arguments("finish the port", ExitReminderChanged.StateOneofCase.Reminder)]
    [Arguments(null, ExitReminderChanged.StateOneofCase.Cleared)]
    public async Task Set_records_and_announces_the_change(
        string? reminder,
        ExitReminderChanged.StateOneofCase expected,
        CancellationToken cancellationToken)
    {
        using var database = SessionDatabase.Open(":memory:");
        var repository = new EventRepository(database);
        using var broker = new EventBroker();
        using var listener = broker.Subscribe();
        IExitReminder exitReminder = new ExitReminder(repository, broker, TestModels.PromptTemplates, "agent");

        await exitReminder.Set(reminder, cancellationToken);

        var announced = await listener.Reader.ReadAsync(cancellationToken);
        _ = await Assert.That(announced.PayloadCase).IsEqualTo(Event.PayloadOneofCase.ExitReminderChanged);
        _ = await Assert.That(announced.ExitReminderChanged.StateCase).IsEqualTo(expected);
        _ = await Assert.That(announced.AgentSessionId).IsEqualTo("agent");
        _ = await Assert.That(repository.LatestExitReminder("agent")).IsEqualTo(reminder);
    }
}
