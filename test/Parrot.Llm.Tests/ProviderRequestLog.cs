using Parrot.Diagnostics;
using Parrot.Llm;

namespace Parrot.Core.Tests;

internal sealed class ProviderRequestLog : IDisposable
{
    private readonly RecordingDiagnosticLog _log = new();

    public ProviderSessions OpenSessions(string agent) => new(_log, agent, null);

    public async Task AssertAttempts(string agent, string[] transports, string[] outcomes, int calls)
    {
        var entries = _log.Entries;
        var owned = entries.Where(entry => entry.AgentSessionId == agent).ToArray();
        var starts = owned.Where(entry => entry.Operation == "request_started").ToArray();
        var finishes = owned.Where(entry => entry.Operation == "request_finished").ToArray();
        var callStarts = owned.Where(entry => entry.Operation == "call_started").ToArray();
        var allCallStarts = entries.Where(entry => entry.Operation == "call_started").ToArray();
        _ = await Assert.That(allCallStarts.Select(entry => entry.CorrelationId).Distinct().Count()).IsEqualTo(allCallStarts.Length);
        _ = await Assert.That(callStarts.Length).IsEqualTo(calls);
        _ = await Assert.That(owned.Count(entry => entry.Operation == "call_finished")).IsEqualTo(calls);
        _ = await Assert.That(starts.Length).IsEqualTo(transports.Length);
        _ = await Assert.That(finishes.Length).IsEqualTo(outcomes.Length);
        _ = await Assert.That(starts.Select(entry => entry.RequestId).Distinct().Count()).IsEqualTo(starts.Length);
        for (var index = 0; index < starts.Length; index++)
        {
            var request = starts[index].RequestId;
            var correlation = starts[index].CorrelationId;
            _ = await Assert.That(request).IsNotNull().And.IsNotEqualTo(string.Empty);
            _ = await Assert.That(finishes[index].RequestId).IsEqualTo(request);
            _ = await Assert.That(finishes[index].CorrelationId).IsEqualTo(correlation);
            _ = await Assert.That(callStarts.Count(entry => entry.CorrelationId == correlation)).IsEqualTo(1);
            _ = await Assert.That(starts[index].Transport).IsEqualTo(transports[index]);
            _ = await Assert.That(finishes[index].Transport).IsEqualTo(transports[index]);
            _ = await Assert.That(finishes[index].Outcome).IsEqualTo(outcomes[index]);
            _ = await Assert.That(finishes[index].DurationMilliseconds).IsNotNull();
            var sizes = owned.Where(entry => entry.Operation == "request_size" && entry.RequestId == request).ToArray();
            _ = await Assert.That(sizes.Length).IsLessThanOrEqualTo(1);
            if (sizes.Length == 1)
            {
                _ = await Assert.That(sizes[0].CorrelationId).IsEqualTo(correlation);
                _ = await Assert.That(sizes[0].Transport).IsEqualTo(transports[index]);
                _ = await Assert.That(sizes[0].RequestBytes).IsEqualTo(finishes[index].RequestBytes);
            }
            else
            {
                _ = await Assert.That(finishes[index].RequestBytes).IsNull();
            }

            _ = await Assert.That(entries.Count(entry => entry.RequestId == request)).IsEqualTo(2 + sizes.Length);
        }
    }

    public async Task AssertByteCounts(string agent, long?[] requestBytes, long?[] responseBytes)
    {
        var finishes = _log.Entries.Where(entry => entry.AgentSessionId == agent && entry.Operation == "request_finished").ToArray();
        _ = await Assert.That(finishes.Length).IsEqualTo(requestBytes.Length);
        _ = await Assert.That(finishes.Length).IsEqualTo(responseBytes.Length);
        for (var index = 0; index < finishes.Length; index++)
        {
            _ = await Assert.That(finishes[index].RequestBytes).IsEqualTo(requestBytes[index]);
            _ = await Assert.That(finishes[index].ResponseBytes).IsEqualTo(responseBytes[index]);
        }
    }

    public void Dispose() => _log.Dispose();

    private sealed class RecordingDiagnosticLog : IDiagnosticLog
    {
        private readonly Lock _gate = new();
        private readonly List<DiagnosticEvent> _entries = [];

        public IReadOnlyList<DiagnosticEvent> Entries
        {
            get
            {
                lock (_gate)
                {
                    return [.. _entries];
                }
            }
        }

        public void Write(DiagnosticEvent entry)
        {
            lock (_gate)
            {
                _entries.Add(entry);
            }
        }

        public void Dispose()
        {
        }
    }
}
