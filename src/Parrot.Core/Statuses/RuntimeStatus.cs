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
        MainAgentProfile profile,
        CancellationToken cancellationToken) =>
        _registry.Observe(
            new StatusQuery(
                session.SessionId,
                profile.Id,
                selection.ResolvedModel.CanonicalModel.Provider.Id,
                selection.ResolvedModel.CanonicalModel.ModelId,
                selection.ResolvedModel.CanonicalModel.Variant?.Name ?? string.Empty),
            new ProfileStatusProvider(
                $"profile:{profile.Id}-mode",
                profile.Prompt,
                [profile.HardRule],
                profile.Status),
            cancellationToken);
}
