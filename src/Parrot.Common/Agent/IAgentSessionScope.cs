using Parrot.AgentTasks;
using Parrot.Process;
using Parrot.Questions;

namespace Parrot.Agent;

/// <summary>Owns an agent session and its session-scoped services, disposing their lifetimes together.</summary>
internal interface IAgentSessionScope : IAsyncDisposable
{
    IAgentSession Session { get; }

    IProcessOwner Processes { get; }

    IGoalService Goals { get; }

    IAgentTaskRunCatalog AgentTaskRuns { get; }

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

    /// <summary>Starts queue snapshot publication once, after the scope is admitted to its topology.</summary>
    void PublishQueueSnapshots();

    /// <summary>Starts process snapshot publication once, after the scope is admitted to its topology.</summary>
    void PublishProcessSnapshots();

    /// <summary>Captures queue snapshot events without creating services or subscribing to updates.</summary>
    IReadOnlyList<Protocol.Event> CaptureQueueSnapshotEvents();

    /// <summary>Captures process snapshot events without creating services or subscribing to updates.</summary>
    IReadOnlyList<Protocol.Event> CaptureProcessSnapshotEvents();
}
