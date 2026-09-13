using Parrot.AgentTasks;
using Parrot.Process;
using Parrot.Questions;
using Parrot.Queues;

namespace Parrot.Agent;

/// <summary>
/// Provides borrowed roots of an agent's composed graph and owns its generated service lifetimes.
/// Synchronous disposal cleans up rejected construction; asynchronous disposal supports normal shutdown.
/// </summary>
internal interface IAgentSessionComposition : IDisposable, IAsyncDisposable
{
    IAgentSession Session { get; }

    IGoalService Goals { get; }

    IAgentSpawner AgentSpawner { get; }

    IAgentTaskRunCatalog AgentTaskRuns { get; }

    IChildRegistry ChildRegistry { get; }

    IAgentParentScope ParentScope { get; }

    IChildQuestionCoordinator ChildQuestions { get; }

    IProcessOwner Processes { get; }

    IAgentQueues Queues { get; }
}
