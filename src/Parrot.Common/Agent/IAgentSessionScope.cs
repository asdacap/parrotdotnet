using Parrot.AgentTasks;
using Parrot.Process;
using Parrot.Questions;
using Parrot.Queues;

namespace Parrot.Agent;

/// <summary>Owns an agent session and its session-scoped services, disposing their lifetimes together.</summary>
internal interface IAgentSessionScope : IAsyncDisposable
{
    IAgentSession Session { get; }

    IProcessOwner Processes { get; }

    IAgentQueues Queues { get; }

    IGoalService Goals { get; }

    IAgentTaskRunCatalog AgentTaskRuns { get; }

    IAgentSpawner AgentSpawner { get; }

    IChildRegistry ChildRegistry { get; }

    IAgentParentScope ParentScope { get; }

    IChildQuestionCoordinator ChildQuestions { get; }

    /// <summary>Publishes local inventories after the scope is admitted to its topology.</summary>
    void PublishInventories();
}
