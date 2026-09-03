using Parrot.Questions;

namespace Parrot.Agent;

internal interface IAgentSessionScope : IAsyncDisposable
{
    AgentSession Session { get; }

    ChildRegistry ChildRegistry { get; }

    ChildQuestionCoordinator ChildQuestions { get; }
}
