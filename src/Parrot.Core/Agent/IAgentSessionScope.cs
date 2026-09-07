using Parrot.Questions;

namespace Parrot.Agent;

/// <summary>Owns an agent session and its session-scoped services, disposing their lifetimes together.</summary>
internal interface IAgentSessionScope : IAsyncDisposable
{
    IAgentSession Session { get; }

    GoalService Goals { get; }

    AgentSpawner AgentSpawner { get; }

    IChildRegistry ChildRegistry { get; }

    AgentSessionParentScope ParentScope { get; }

    ChildQuestionCoordinator ChildQuestions { get; }
}
