using Parrot.Cli.Enhanced;
using Parrot.Protocol;

namespace Parrot.Cli.Tests;

internal sealed class SessionUsageSnapshotStreamReaderTests
{
    [Test]
    public async Task Snapshots_update_modeline_usage_and_are_not_rendered(
        CancellationToken cancellationToken)
    {
        var stream = new ChannelStreamWriter<Event>();
        await stream.WriteAsync(
            new Event
            {
                SessionUsageSnapshot = new SessionUsageSnapshot { Revision = 3, InputTokens = 2400 },
            },
            cancellationToken);
        await stream.WriteAsync(
            new Event { AgentSessionId = "root", TurnStarted = new TurnStarted() },
            cancellationToken);
        stream.Complete();

        var received = new List<SessionUsageSnapshot>();
        var reader = new SessionUsageSnapshotStreamReader(
            stream.Reader,
            (snapshot, _) =>
            {
                received.Add(snapshot.Clone());
                return Task.CompletedTask;
            });

        _ = await Assert.That(await reader.MoveNext(cancellationToken)).IsTrue();
        _ = await Assert.That(reader.Current.PayloadCase).IsEqualTo(Event.PayloadOneofCase.TurnStarted);
        _ = await Assert.That(received).Count().IsEqualTo(1);
        _ = await Assert.That(received[0].InputTokens).IsEqualTo(2400);
        _ = await Assert.That(await reader.MoveNext(cancellationToken)).IsFalse();
    }
}
