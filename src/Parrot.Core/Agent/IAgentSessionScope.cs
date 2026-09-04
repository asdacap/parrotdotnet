using Parrot.Questions;

namespace Parrot.Agent;

internal interface IAgentSessionScope : IAsyncDisposable
{
    IAgentSession Session { get; }

    IChildRegistry ChildRegistry { get; }

    ChildQuestionCoordinator ChildQuestions { get; }
}
