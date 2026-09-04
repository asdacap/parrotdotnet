using Parrot.Questions;

namespace Parrot.Agent;

internal interface IAgentSessionScope : IAsyncDisposable
{
    IAgentSession Session { get; }

    ChildRegistry ChildRegistry { get; }

    ChildQuestionCoordinator ChildQuestions { get; }
}
