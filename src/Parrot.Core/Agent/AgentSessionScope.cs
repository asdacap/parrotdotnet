using Parrot.AgentTasks;
using Parrot.Process;
using Parrot.Questions;

namespace Parrot.Agent;

internal sealed class AgentSessionScope : IAgentSessionScope
{
    private readonly AgentSessionComposition _composition;
    private readonly Parrot.Diagnostics.IDiagnosticLog _diagnostics;
    private readonly string _sessionId;

    internal AgentSessionScope(AgentSessionScopeArguments arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        _diagnostics = arguments.Diagnostics;
        _sessionId = arguments.Identity.SessionId;
        _composition = new AgentSessionComposition(arguments, this);
        try
        {
            ParentScope.Validate(Session.Identity);
            ParentScope.ValidateOwnerScope(this);
            _diagnostics.Write(new("agent", "created", Parrot.Diagnostics.DiagnosticSeverity.Information)
            {
                AgentSessionId = _sessionId,
            });
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

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _composition.DisposeAsync().ConfigureAwait(false);
            _diagnostics.Write(new("agent", "closed", Parrot.Diagnostics.DiagnosticSeverity.Information)
            {
                AgentSessionId = _sessionId,
            });
        }
        catch (Exception failure)
        {
            _diagnostics.Write(new("agent", "close_failed", Parrot.Diagnostics.DiagnosticSeverity.Error)
            {
                AgentSessionId = _sessionId,
                ErrorCode = Parrot.Diagnostics.DiagnosticEvent.ClassifyFailure(failure),
            });
            throw;
        }
    }

    internal void DisposeRejectedConstruction() => _composition.Dispose();
}
