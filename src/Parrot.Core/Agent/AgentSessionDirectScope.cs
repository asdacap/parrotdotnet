using Parrot.Config;
using Parrot.Questions;

namespace Parrot.Agent;

internal sealed class AgentSessionDirectScope : IAgentSessionScope
{
    private bool _disposed;

    private AgentSessionDirectScope(
        string ownerSessionId,
        AgentRegistry registry,
        PromptTemplateCatalog promptTemplates,
        Func<ChildQuestionCoordinator, AgentSession> buildSession)
    {
        ChildQuestions = new ChildQuestionCoordinator(ownerSessionId, registry, promptTemplates);
        try
        {
            Session = buildSession(ChildQuestions);
        }
        catch
        {
            ChildQuestions.Dispose();
            throw;
        }
    }

    public AgentSession Session { get; }

    public ChildQuestionCoordinator ChildQuestions { get; }

    public static AgentSessionDirectScope Build(
        string ownerSessionId,
        AgentRegistry registry,
        PromptTemplateCatalog promptTemplates,
        Func<ChildQuestionCoordinator, AgentSession> buildSession) =>
        new(ownerSessionId, registry, promptTemplates, buildSession);

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            ChildQuestions.Dispose();
        }

        return ValueTask.CompletedTask;
    }
}
