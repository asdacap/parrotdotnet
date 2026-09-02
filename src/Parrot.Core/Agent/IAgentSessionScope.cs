using Parrot.Questions;

namespace Parrot.Agent;

internal interface IAgentSessionScope : IAsyncDisposable
{
    AgentSession Session { get; }

    ChildQuestionCoordinator ChildQuestions { get; }
}
