using Parrot.Questions;

namespace Parrot.Agent;

/// <summary>Owns an agent session and its session-scoped services, disposing their lifetimes together.</summary>
internal interface IAgentSessionScope : IAsyncDisposable
{
    IAgentSession Session { get; }

    IGoalService Goals { get; }

    IAgentSpawner AgentSpawner { get; }

    IChildRegistry ChildRegistry { get; }

    IAgentParentScope ParentScope { get; }

    IChildQuestionCoordinator ChildQuestions { get; }

    /// <summary>
    /// Returns the existing service registered under exactly T, or throws InvalidOperationException when absent.
    /// The caller borrows the instance without taking ownership; lookup remains available during and after shutdown.
    /// </summary>
    T GetService<T>()
        where T : class;

    /// <summary>Starts every registered inventory publisher once, after the scope is admitted to its topology.</summary>
    void PublishSnapshots();

    /// <summary>Captures every registered inventory as replayable events, in publisher registration order.</summary>
    IReadOnlyList<Protocol.Event> CaptureSnapshotEvents();

    /// <summary>Prevents new work in every registered work owner and waits for it to settle, in registration order.</summary>
    Task SettleWork();
}
