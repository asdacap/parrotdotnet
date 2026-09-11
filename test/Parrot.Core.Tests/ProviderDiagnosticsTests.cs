using Parrot.Context;
using Parrot.Diagnostics;
using Parrot.Llm;
using Parrot.State;
using Parrot.Store;

namespace Parrot.Core.Tests;

internal sealed class ProviderDiagnosticsTests
{
    [Test]
    [Arguments("completed")]
    [Arguments("transport_retry")]
    [Arguments("failed")]
    [Arguments("cancelled")]
    [Arguments("disposed")]
    [Arguments("dispose_failed")]
    [Arguments("open_failed")]
    public async Task Calls_preserve_stream_results_and_record_safe_terminal_metadata(string behavior)
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(directory);
        try
        {
            var resources = new UserSessionResources(
                new StatePaths(directory, directory, directory),
                UserSessionId.Parse("provider-session"),
                ProjectWorkspace.FromLaunchDirectory(directory));
            using var log = FileDiagnosticLog.OpenSession(resources, "provider-instance", TextWriter.Null, TimeProvider.System);
            using var cancellation = new CancellationTokenSource();
            if (behavior == "cancelled")
            {
                await cancellation.CancelAsync();
            }

            Exception expected = behavior == "cancelled"
                ? new OperationCanceledException("private-sentinel", cancellation.Token)
                : new InvalidOperationException("private-sentinel");
            var provider = new DiagnosticTestProvider(behavior, expected);
            var sessions = new ProviderSessions(log, "agent-provider");
            Exception? received = null;
            var emitted = new List<LLMEvent>();
            try
            {
                await foreach (var published in sessions.Get(behavior == "transport_retry" ? new RetryingProvider(provider) : provider).Call(
                    new LLMRequest { Model = "model", Messages = [LLMMessage.User("private-sentinel")] },
                    cancellation.Token))
                {
                    emitted.Add(published);
                    if (behavior == "disposed")
                    {
                        break;
                    }
                }
            }
            catch (Exception failure)
            {
                received = failure;
            }

            var lines = await File.ReadAllLinesAsync(resources.LogPath);
            var text = string.Join('\n', lines);
            var failed = behavior is "failed" or "cancelled" or "dispose_failed" or "open_failed";
            _ = await Assert.That(received).IsSameReferenceAs(failed ? expected : null);
            _ = await Assert.That(provider.Session.Disposals).IsEqualTo(behavior == "open_failed" ? 0 : behavior == "transport_retry" ? 2 : 1);
            _ = await Assert.That(provider.Session.CancellationToken).IsEqualTo(cancellation.Token);
            _ = await Assert.That(lines.Count(line => line.Contains("event=\"call_started\"", StringComparison.Ordinal))).IsEqualTo(1);
            _ = await Assert.That(lines.Count(line => line.Contains("event=\"call_finished\"", StringComparison.Ordinal))).IsEqualTo(1);
            var expectedOutcome = behavior == "transport_retry" ? "completed" : behavior is "dispose_failed" or "open_failed" ? "failed" : behavior;
            _ = await Assert.That(lines[^1].Contains($"outcome=\"{expectedOutcome}\"", StringComparison.Ordinal)).IsTrue();
            _ = await Assert.That(text.Contains("private-sentinel", StringComparison.Ordinal)).IsFalse();
            _ = await Assert.That(lines.All(line => line.Contains("agent=\"agent-provider\"", StringComparison.Ordinal))).IsTrue();
            _ = await Assert.That(lines.Select(line => line.Split("correlation=\"", StringSplitOptions.None)[1].Split('"')[0]).Distinct().Count()).IsEqualTo(1);
            _ = await Assert.That(lines[^1].Contains("duration_ms=\"", StringComparison.Ordinal)).IsTrue();
            if (behavior is "completed" or "transport_retry")
            {
                var providerEvents = behavior == "transport_retry" ? emitted.Skip(1) : emitted;
                _ = await Assert.That(providerEvents.SequenceEqual(provider.Session.Events)).IsTrue();
                _ = await Assert.That(text.Contains("event=\"call_retry\"", StringComparison.Ordinal)).IsTrue();
                _ = await Assert.That(text.Contains("event=\"input_tokens\"", StringComparison.Ordinal)).IsTrue();
                _ = await Assert.That(text.Contains("count=\"17\"", StringComparison.Ordinal)).IsTrue();
                _ = await Assert.That(text.Contains("event=\"cached_input_tokens\"", StringComparison.Ordinal)).IsTrue();
                _ = await Assert.That(text.Contains("event=\"output_tokens\"", StringComparison.Ordinal)).IsTrue();
            }

            if (behavior == "transport_retry")
            {
                _ = await Assert.That(emitted[0]).IsEqualTo(LLMEvent.Retry(
                    1, TimeSpan.FromMilliseconds(200), "Provider connection or protocol failure. Retrying."));
                _ = await Assert.That(lines.Count(line => line.Contains("event=\"call_retry\"", StringComparison.Ordinal))).IsEqualTo(2);
            }

            if (behavior == "cancelled")
            {
                _ = await Assert.That(lines[^1].Contains(" INFO ", StringComparison.Ordinal)).IsTrue();
            }
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Test]
    public async Task Shared_provider_calls_remain_in_their_own_session_logs(CancellationToken cancellationToken)
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(directory);
        try
        {
            var paths = new StatePaths(directory, directory, directory);
            var workspace = ProjectWorkspace.FromLaunchDirectory(directory);
            var first = new UserSessionResources(paths, UserSessionId.Parse("first-session"), workspace);
            var second = new UserSessionResources(paths, UserSessionId.Parse("second-session"), workspace);
            using var firstLog = FileDiagnosticLog.OpenSession(first, "provider-instance", TextWriter.Null, TimeProvider.System);
            using var secondLog = FileDiagnosticLog.OpenSession(second, "provider-instance", TextWriter.Null, TimeProvider.System);
            var provider = new DiagnosticTestProvider("completed", new InvalidOperationException("private-sentinel"));
            var firstSessions = new ProviderSessions(firstLog, "first-agent");
            var secondSessions = new ProviderSessions(secondLog, "second-agent");
            foreach (var sessions in new[] { firstSessions, secondSessions })
            {
                await foreach (var published in sessions.Get(provider).Call(
                    new LLMRequest { Model = "model", Messages = [] }, cancellationToken))
                {
                    _ = published;
                }

                await sessions.Close();
            }

            var firstText = await File.ReadAllTextAsync(first.LogPath, cancellationToken);
            var secondText = await File.ReadAllTextAsync(second.LogPath, cancellationToken);
            _ = await Assert.That(firstText.Contains("first-agent", StringComparison.Ordinal)).IsTrue();
            _ = await Assert.That(firstText.Contains("second-agent", StringComparison.Ordinal)).IsFalse();
            _ = await Assert.That(secondText.Contains("second-agent", StringComparison.Ordinal)).IsTrue();
            _ = await Assert.That(secondText.Contains("first-agent", StringComparison.Ordinal)).IsFalse();
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Test]
    public async Task Compactor_helper_logs_provider_calls_and_usage_without_summary_payloads(CancellationToken cancellationToken)
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(directory);
        try
        {
            var resources = new UserSessionResources(
                new StatePaths(directory, directory, directory),
                UserSessionId.Parse("compactor-session"),
                ProjectWorkspace.FromLaunchDirectory(directory));
            using var log = FileDiagnosticLog.OpenSession(resources, "provider-instance", TextWriter.Null, TimeProvider.System);
            var provider = new DiagnosticTestProvider("completed", new InvalidOperationException("private-sentinel"));
            var model = new ProviderModel(provider, new LLMModel("model", provider.Id) { ContextWindow = 1_000 });
            var history = Enumerable.Range(0, 6)
                .Select(_ => LLMMessage.User("private-sentinel" + new string('x', 300)))
                .ToList();
            var compactor = new Compactor(90, 30, 60_000, 1024, TestModels.PromptTemplates);
            var result = await compactor.Compact(
                model,
                "private-sentinel",
                [],
                history,
                LLMMessage.User("fixed"),
                TestModels.CompactionGroupBlobs(),
                log,
                "compactor-agent",
                cancellationToken);
            _ = await Assert.That(result).IsNotNull();
            var text = await File.ReadAllTextAsync(resources.LogPath, cancellationToken);
            _ = await Assert.That(text.Contains("event=\"call_started\"", StringComparison.Ordinal)).IsTrue();
            _ = await Assert.That(text.Contains("event=\"call_finished\"", StringComparison.Ordinal)).IsTrue();
            _ = await Assert.That(text.Contains("event=\"call_retry\"", StringComparison.Ordinal)).IsTrue();
            _ = await Assert.That(text.Contains("event=\"output_tokens\"", StringComparison.Ordinal)).IsTrue();
            _ = await Assert.That(text.Contains("agent=\"compactor-agent\"", StringComparison.Ordinal)).IsTrue();
            _ = await Assert.That(text.Contains("private-sentinel", StringComparison.Ordinal)).IsFalse();
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private sealed class DiagnosticTestProvider(string behavior, Exception failure) : ILLMProvider
    {
        public DiagnosticTestSession Session { get; } = new(behavior, failure);

        public string Id => "diagnostic-provider";

        public IReadOnlyList<LLMModel> SeedModels() => [];

        public ValueTask<bool> HasCredential(CancellationToken cancellationToken) => ValueTask.FromResult(true);

        public Task<IReadOnlyList<LLMModel>> ListModels(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<LLMModel>>([]);

        public ILLMProviderSession OpenSession() => Session;

        public IAsyncEnumerable<LLMEvent> Call(LLMRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Shared provider must not be called.");
    }

    private sealed class DiagnosticTestSession(string behavior, Exception failure) :
        ILLMProviderSession, IAsyncEnumerable<LLMEvent>, IAsyncEnumerator<LLMEvent>
    {
        private int _position = -1;

        public LLMEvent[] Events { get; } =
        [
            LLMEvent.Retry(1, TimeSpan.Zero, "private-sentinel"),
            LLMEvent.Completed("private-sentinel", 17, 3, 5, "private-sentinel", []),
        ];

        public int Disposals { get; private set; }

        public CancellationToken CancellationToken { get; private set; }

        public LLMEvent Current => Events[_position];

        public void BeginTurn()
        {
        }

        public ValueTask<bool> MoveNextAsync() => behavior == "transport_retry" && Disposals == 0
            ? ValueTask.FromException<bool>(new IOException("private-sentinel"))
            : behavior is "failed" or "cancelled"
                ? ValueTask.FromException<bool>(failure)
                : ValueTask.FromResult(++_position < Events.Length);

        public IAsyncEnumerable<LLMEvent> Call(LLMRequest request, CancellationToken cancellationToken)
        {
            CancellationToken = cancellationToken;
            return behavior == "open_failed" ? throw failure : this;
        }

        public IAsyncEnumerator<LLMEvent> GetAsyncEnumerator(CancellationToken cancellationToken) => this;

        ValueTask IAsyncDisposable.DisposeAsync()
        {
            Disposals++;
            return behavior == "dispose_failed" ? ValueTask.FromException(failure) : ValueTask.CompletedTask;
        }
    }
}
