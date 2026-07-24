using Parrot.Agent;

namespace Parrot.Statuses;

internal sealed class RuntimeStatus(UserSession owner)
{
    private readonly StatusRegistry _registry = new(
        new SelectionStatusProvider(),
        new ActiveWorkStatusProvider(owner.ShellProcesses, owner.Registry));

    public Task<string> Observe(
        AgentSession session,
        AgentSelection selection,
        ModeProfile mode,
        CancellationToken cancellationToken) =>
        _registry.Observe(
            new StatusQuery(
                session.SessionId,
                mode.Id,
                selection.Provider.Id,
                selection.Model,
                string.Empty),
            new ProfileStatusProvider(
                $"profile:{mode.Id}-mode",
                mode.Prompt,
                [mode.HardRule],
                mode.Status),
            cancellationToken);
}
