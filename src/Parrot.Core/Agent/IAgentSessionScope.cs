using Parrot.Process;
using Parrot.Questions;
using Parrot.Queues;

namespace Parrot.Agent;

/// <summary>Owns an agent session and its session-scoped services, disposing their lifetimes together.</summary>
internal interface IAgentSessionScope : IAsyncDisposable
{
    IAgentSession Session { get; }

    ShellProcessOwner Processes { get; }

    AgentQueues Queues { get; }

    GoalService Goals { get; }

    AgentSpawner AgentSpawner { get; }

    IChildRegistry ChildRegistry { get; }

    AgentSessionParentScope ParentScope { get; }

    ChildQuestionCoordinator ChildQuestions { get; }

    /// <summary>Publishes local inventories after the scope is admitted to its topology.</summary>
    void PublishInventories();
}
