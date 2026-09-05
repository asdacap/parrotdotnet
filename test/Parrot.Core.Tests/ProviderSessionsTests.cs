using Parrot.Llm;

namespace Parrot.Core.Tests;

internal sealed class ProviderSessionsTests
{
    private static readonly LLMRequest Request = new() { Model = "model", Messages = [] };

    [Test]
    public async Task Sessions_are_lazy_shared_within_an_owner_and_isolated_across_owners(
        CancellationToken cancellationToken)
    {
        var provider = new SessionProvider();
        var firstOwner = new ProviderSessions();
        var secondOwner = new ProviderSessions();

        var first = firstOwner.Get(provider);
        var again = firstOwner.Get(provider);
        var second = secondOwner.Get(provider);
        _ = await Drain(first, cancellationToken);
        _ = await Drain(again, cancellationToken);
        _ = await Drain(second, cancellationToken);

        _ = await Assert.That(first).IsSameReferenceAs(again);
        _ = await Assert.That(first).IsNotSameReferenceAs(second);
        _ = await Assert.That(provider.Opened).IsEqualTo(2);
        _ = await Assert.That(provider.DirectCalls).IsEqualTo(0);
        _ = await Assert.That(((SessionProviderSession)first).Calls).IsEqualTo(2);
        _ = await Assert.That(((SessionProviderSession)second).Calls).IsEqualTo(1);

        await firstOwner.Close();
        _ = await Assert.That(((SessionProviderSession)first).Disposed).IsTrue();
        _ = await Assert.That(((SessionProviderSession)second).Disposed).IsFalse();
        _ = await Assert.That(() => firstOwner.Get(provider)).Throws<ObjectDisposedException>();

        await secondOwner.Close();
        await secondOwner.Close();
        _ = await Assert.That(((SessionProviderSession)second).Disposed).IsTrue();
        _ = await Assert.That(((SessionProviderSession)second).DisposeCalls).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Concurrent_closes_await_one_completed_disposal_and_repeat_the_result(
        bool fail,
        CancellationToken cancellationToken)
    {
        var expected = fail ? new InvalidOperationException("dispose failed") : null;
        var provider = new BlockingSessionProvider(expected);
        var sessions = new ProviderSessions();
        _ = sessions.Get(provider);

        var firstClose = sessions.Close().AsTask();
        await provider.Session.DisposalStarted.WaitAsync(cancellationToken);
        var secondClose = sessions.Close().AsTask();

        _ = await Assert.That(firstClose.IsCompleted).IsFalse();
        _ = await Assert.That(secondClose.IsCompleted).IsFalse();
        _ = await Assert.That(provider.Session.DisposeCalls).IsEqualTo(1);

        provider.Session.ReleaseDisposal();
        if (expected is null)
        {
            await firstClose.WaitAsync(cancellationToken);
            await secondClose.WaitAsync(cancellationToken);
            await sessions.Close();
        }
        else
        {
            _ = await Assert.That(() => firstClose.WaitAsync(cancellationToken)).ThrowsExactly<InvalidOperationException>()
                .WithMessage(expected.Message);
            _ = await Assert.That(() => secondClose.WaitAsync(cancellationToken)).ThrowsExactly<InvalidOperationException>()
                .WithMessage(expected.Message);
            _ = await Assert.That(() => sessions.Close().AsTask()).ThrowsExactly<InvalidOperationException>()
                .WithMessage(expected.Message);
        }

        _ = await Assert.That(provider.Session.DisposeCalls).IsEqualTo(1);
    }

    [Test]
    public async Task Begin_turn_notifies_existing_and_lazy_sessions(CancellationToken cancellationToken)
    {
        var firstProvider = new SessionProvider();
        var secondProvider = new SessionProvider();
        var sessions = new ProviderSessions();
        var existing = sessions.Get(firstProvider);

        sessions.BeginTurn();
        var lazy = sessions.Get(secondProvider);
        sessions.BeginTurn();
        await sessions.Close();

        _ = await Assert.That(((SessionProviderSession)existing).BeginTurnCalls).IsEqualTo(2);
        _ = await Assert.That(((SessionProviderSession)lazy).BeginTurnCalls).IsEqualTo(2);
        _ = await Assert.That(firstProvider.Opened).IsEqualTo(1);
        _ = await Assert.That(secondProvider.Opened).IsEqualTo(1);
        _ = await Assert.That(cancellationToken.IsCancellationRequested).IsFalse();
    }

    [Test]
    public async Task Retrying_provider_opens_and_disposes_one_inner_session(CancellationToken cancellationToken)
    {
        var provider = new SessionProvider();
        var sessions = new ProviderSessions();
        var retrying = new RetryingProvider(provider);

        var session = sessions.Get(retrying);
        _ = await Drain(session, cancellationToken);
        _ = await Drain(session, cancellationToken);
        await sessions.Close();

        _ = await Assert.That(provider.Opened).IsEqualTo(1);
        _ = await Assert.That(provider.DirectCalls).IsEqualTo(0);
        _ = await Assert.That(provider.Sessions.Single().Calls).IsEqualTo(2);
        _ = await Assert.That(provider.Sessions.Single().DisposeCalls).IsEqualTo(1);
    }

    private static async Task<List<LLMEvent>> Drain(
        ILLMProviderSession session,
        CancellationToken cancellationToken)
    {
        var events = new List<LLMEvent>();
        await foreach (var published in session.Call(Request, cancellationToken))
        {
            events.Add(published);
        }

        return events;
    }

    private sealed class BlockingSessionProvider(Exception? failure) : ILLMProvider
    {
        public BlockingProviderSession Session { get; } = new(failure);

        public string Id => "blocking-session-provider";

        public ValueTask<bool> HasCredential(CancellationToken cancellationToken) => ValueTask.FromResult(true);

        public Task<IReadOnlyList<LLMModel>> ListModels(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<LLMModel>>([]);

        public ILLMProviderSession OpenSession() => Session;

        public IAsyncEnumerable<LLMEvent> Call(LLMRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Scoped calls must not use the shared provider directly.");
    }

    private sealed class BlockingProviderSession(Exception? failure) : ILLMProviderSession
    {
        private readonly TaskCompletionSource _disposalStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _releaseDisposal =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task DisposalStarted => _disposalStarted.Task;

        public int DisposeCalls { get; private set; }

        public int BeginTurnCalls { get; private set; }

        public void BeginTurn() => BeginTurnCalls++;

        public IAsyncEnumerable<LLMEvent> Call(LLMRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Calls are not expected.");

        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            _disposalStarted.SetResult();
            return new ValueTask(FinishDisposal());
        }

        public void ReleaseDisposal() => _releaseDisposal.SetResult();

        private Task FinishDisposal() => _releaseDisposal.Task.ContinueWith(
            _ => failure is null ? Task.CompletedTask : Task.FromException(failure),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default).Unwrap();
    }

    private sealed class SessionProvider : ILLMProvider
    {
        public List<SessionProviderSession> Sessions { get; } = [];

        public int Opened { get; private set; }

        public int DirectCalls { get; private set; }

        public string Id => "session-provider";

        public ValueTask<bool> HasCredential(CancellationToken cancellationToken) => ValueTask.FromResult(true);

        public Task<IReadOnlyList<LLMModel>> ListModels(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<LLMModel>>([]);

        public ILLMProviderSession OpenSession()
        {
            Opened++;
            var session = new SessionProviderSession();
            Sessions.Add(session);
            return session;
        }

        public IAsyncEnumerable<LLMEvent> Call(LLMRequest request, CancellationToken cancellationToken)
        {
            DirectCalls++;
            throw new InvalidOperationException("Scoped calls must not use the shared provider directly.");
        }
    }

    private sealed class SessionProviderSession : ILLMProviderSession
    {
        public int Calls { get; private set; }

        public int DisposeCalls { get; private set; }

        public int BeginTurnCalls { get; private set; }

        public bool Disposed => DisposeCalls > 0;

        public void BeginTurn() => BeginTurnCalls++;

        public async IAsyncEnumerable<LLMEvent> Call(
            LLMRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Disposed, this);
            Calls++;
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return LLMEvent.Completed("stop", 1, 0, 1, "reply", []);
        }

        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            return ValueTask.CompletedTask;
        }
    }
}
