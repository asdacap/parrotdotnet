using Parrot.Cli.Enhanced;
using Parrot.Protocol;

namespace Parrot.Cli.Tests;

internal sealed class ProviderCallUsageStreamReaderTests
{
    [Test]
    public async Task Usage_is_consumed_and_never_forwarded(CancellationToken cancellationToken)
    {
        var stream = new ChannelStreamWriter<Event>();
        await stream.WriteAsync(
            new Event
            {
                AgentSessionId = "child",
                ProviderCallUsage = new ProviderCallUsage { InputTokens = 1, OutputTokens = 2 },
            },
            cancellationToken);
        await stream.WriteAsync(new Event { TurnStarted = new TurnStarted() }, cancellationToken);
        stream.Complete();
        var received = new List<ProviderCallUsage>();
        var reader = new ProviderCallUsageStreamReader(stream.Reader, (usage, _) =>
        {
            received.Add(usage.Clone());
            return Task.CompletedTask;
        });

        _ = await Assert.That(await reader.MoveNext(cancellationToken)).IsTrue();
        _ = await Assert.That(reader.Current.PayloadCase).IsEqualTo(Event.PayloadOneofCase.TurnStarted);
        _ = await Assert.That(received).Count().IsEqualTo(1);
        _ = await Assert.That(received[0].InputTokens).IsEqualTo(1);
        _ = await Assert.That(received[0].OutputTokens).IsEqualTo(2);
        _ = await Assert.That(await reader.MoveNext(cancellationToken)).IsFalse();
    }
}
