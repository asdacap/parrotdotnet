using Parrot.Questions;

namespace Parrot.Agent;

internal interface IAgentSessionScope : IAsyncDisposable
{
    IAgentSession Session { get; }

    GoalService Goals { get; }

    AgentSpawner AgentSpawner { get; }

    IChildRegistry ChildRegistry { get; }

    AgentSessionParentScope ParentScope { get; }

    ChildQuestionCoordinator ChildQuestions { get; }
}
