using Parrot.Config;
using Parrot.Questions;

namespace Parrot.Agent;

internal sealed class AgentSessionDirectScope : IAgentSessionScope
{
    private bool _disposed;

    private AgentSessionDirectScope(
        AgentIdentity owner,
        AgentRegistry registry,
        PromptTemplateCatalog promptTemplates,
        Func<ChildRegistry, ChildQuestionCoordinator, AgentSession> buildSession)
    {
        ChildRegistry = new ChildRegistry(owner, registry);
        ChildQuestions = new ChildQuestionCoordinator(ChildRegistry, promptTemplates);
        try
        {
            Session = buildSession(ChildRegistry, ChildQuestions);
            ChildRegistry.ValidateOwner(Session.Identity);
            if (!ReferenceEquals(Session.ChildRegistry, ChildRegistry))
            {
                throw new AgentRegistryException("agent session does not retain its scope child registry");
            }
        }
        catch
        {
            ChildQuestions.Dispose();
            throw;
        }
    }

    public AgentSession Session { get; }

    public ChildRegistry ChildRegistry { get; }

    public ChildQuestionCoordinator ChildQuestions { get; }

    public static AgentSessionDirectScope Build(
        AgentIdentity owner,
        AgentRegistry registry,
        PromptTemplateCatalog promptTemplates,
        Func<ChildRegistry, ChildQuestionCoordinator, AgentSession> buildSession) =>
        new(owner, registry, promptTemplates, buildSession);

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
