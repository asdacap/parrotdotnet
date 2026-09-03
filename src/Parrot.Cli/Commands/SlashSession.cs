using Parrot.Config;
using Parrot.Protocol;
using GeneratedParrot = Parrot.Protocol.Parrot;

namespace Parrot.Cli.Commands;

internal sealed class SlashSession(
    GeneratedParrot.ParrotClient client,
    UserSession initialSession,
    Configuration configuration,
    bool interactivePermissions,
    ISlashSessionBinding binding) : ISlashSession
{
    private UserSession _current = initialSession;

    public string Id => _current.Id;

    public string Model => _current.Model;

    public string Mode => _current.Mode;

    public async Task SelectModel(string model, CancellationToken cancellationToken)
    {
        _current = await client.UpdateSessionAsync(
            new UpdateSessionRequest { UserSessionId = Id, Model = model },
            cancellationToken: cancellationToken);
        configuration.SetModel(_current.Model);
    }

    public async Task SelectMode(string mode, CancellationToken cancellationToken) =>
        _current = await client.UpdateSessionAsync(
            new UpdateSessionRequest { UserSessionId = Id, Mode = mode },
            cancellationToken: cancellationToken);

    public Task SetGoal(string goal, CancellationToken cancellationToken) =>
        client.SetGoalAsync(
            new SetGoalRequest { UserSessionId = Id, Goal = goal },
            cancellationToken: cancellationToken).ResponseAsync;

    public Task ClearGoal(CancellationToken cancellationToken) =>
        client.SetGoalAsync(
            new SetGoalRequest { UserSessionId = Id, Clear = new ClearGoal() },
            cancellationToken: cancellationToken).ResponseAsync;

    public async Task StartNew(string model, string mode, CancellationToken cancellationToken)
    {
        var session = await client.CreateSessionAsync(
            new CreateSessionRequest
            {
                Model = model,
                Mode = mode,
                InteractivePermissions = interactivePermissions,
            },
            cancellationToken: cancellationToken);
        var previous = _current;
        _current = session;
        try
        {
            await binding.Replace(session, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _current = previous;
            throw;
        }
    }
}
