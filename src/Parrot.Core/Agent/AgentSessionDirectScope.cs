using Parrot.Config;
using Parrot.Questions;

namespace Parrot.Agent;

internal sealed class AgentSessionDirectScope : IAgentSessionScope
{
    private readonly Lock _gate = new();
    private Task? _shutdown;

    private AgentSessionDirectScope(
        AgentIdentity owner,
        AgentSessionParentScope parentScope,
        AgentRegistry registry,
        PromptTemplateCatalog promptTemplates,
        Func<AgentSessionParentScope, ChildRegistry, ChildQuestionCoordinator, AgentSession> buildSession)
    {
        ChildRegistry = new ChildRegistry(owner, registry);
        ChildQuestions = new ChildQuestionCoordinator(ChildRegistry, promptTemplates);
        try
        {
            Session = buildSession(parentScope, ChildRegistry, ChildQuestions);
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
        AgentSessionParentScope parentScope,
        AgentRegistry registry,
        PromptTemplateCatalog promptTemplates,
        Func<AgentSessionParentScope, ChildRegistry, ChildQuestionCoordinator, AgentSession> buildSession) =>
        new(owner, parentScope, registry, promptTemplates, buildSession);

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _shutdown ??= ShutDown();
            return new ValueTask(_shutdown);
        }
    }

    private async Task ShutDown()
    {
        try
        {
            await ChildRegistry.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            ChildQuestions.Dispose();
        }
    }
}
