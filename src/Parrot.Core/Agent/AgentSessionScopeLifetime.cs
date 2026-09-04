using System.Runtime.ExceptionServices;
using Parrot.Questions;
using Parrot.Queues;

namespace Parrot.Agent;

internal sealed class AgentSessionScopeLifetime(
    IChildRegistry childRegistry,
    IAgentSession session,
    ChildQuestionCoordinator childQuestions,
    AgentQueues queues) : IAsyncDisposable
{
    private readonly Lock _gate = new();
    private Task? _shutdown;

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _shutdown ??= ShutDown();
            return new ValueTask(_shutdown);
        }
    }

    internal void DisposeRejectedConstruction()
    {
        childQuestions.Dispose();
        queues.Dispose();
        _shutdown = childRegistry.DisposeAsync().AsTask();
    }

    private async Task ShutDown()
    {
        Exception? failure = null;
        try
        {
            await childRegistry.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        try
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure ??= exception;
        }

        try
        {
            childQuestions.Dispose();
        }
        catch (Exception exception)
        {
            failure ??= exception;
        }

        try
        {
            queues.Dispose();
        }
        catch (Exception exception)
        {
            failure ??= exception;
        }

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
