using Parrot.Questions;

namespace Parrot.Agent;

internal interface IAgentSessionScope : IAsyncDisposable
{
    IAgentSession Session { get; }

    GoalService Goals { get; }

    IChildRegistry ChildRegistry { get; }

    ChildQuestionCoordinator ChildQuestions { get; }
}
