using Parrot.Agent;

namespace Parrot.Statuses;

internal sealed class RuntimeStatus(UserSession owner)
{
    private readonly StatusRegistry _registry = new(
        new SelectionStatusProvider(),
        new ActiveWorkStatusProvider(owner.ShellProcesses, owner.Registry));

    public Task<string> Observe(
        AgentSession session,
        AgentTurnSelection selection,
        AgentProfile profile,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(profile);

        var query = new StatusQuery(
            session.SessionId,
            session.ParentSessionId,
            session.ParentSessionName,
            profile.Id,
            selection.ResolvedModel.CanonicalModel.Provider.Id,
            selection.ResolvedModel.CanonicalModel.ModelId,
            selection.ResolvedModel.CanonicalModel.Variant?.Name ?? string.Empty);
        var status = new ProfileStatusProvider(
            $"profile:{profile.Id}-mode",
            profile.Prompt,
            [profile.HardRule],
            profile.Status);
        return _registry.Observe(query, status, cancellationToken);
    }
}
