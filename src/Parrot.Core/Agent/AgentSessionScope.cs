using Parrot.AgentTasks;
using Parrot.Process;
using Parrot.Questions;

namespace Parrot.Agent;

internal sealed class AgentSessionScope : IAgentSessionScope
{
    private readonly AgentSessionComposition _composition;

    internal AgentSessionScope(AgentSessionScopeArguments arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        _composition = new AgentSessionComposition(arguments, this);
        try
        {
            ChildRegistry.ValidateOwner(Session.Identity);
            ParentScope.Validate(Session.Identity);
            ParentScope.ValidateOwnerScope(this);
        }
        catch
        {
            _composition.Dispose();
            throw;
        }
    }

    public IAgentSession Session => _composition.Session;

    public GoalService Goals => _composition.Goals;

    public AgentSpawner AgentSpawner => _composition.AgentSpawner;

    public IChildRegistry ChildRegistry => _composition.ChildRegistry;

    public AgentSessionParentScope ParentScope => _composition.ParentScope;

    public ChildQuestionCoordinator ChildQuestions => _composition.ChildQuestions;

    internal AgentTaskRunOwner AgentTaskRuns => _composition.AgentTaskRuns;

    internal ShellProcessOwner Processes => _composition.Processes;

    public ValueTask DisposeAsync() => _composition.DisposeAsync();

    internal void DisposeRejectedConstruction() => _composition.Dispose();
}
