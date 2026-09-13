using Parrot.Config;
using Parrot.Protocol;
using GeneratedParrot = Parrot.Protocol.Parrot;
using ProtocolSkill = Parrot.Protocol.Skill;

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

    public static ISlashSession Create(
        GeneratedParrot.ParrotClient client,
        UserSession initialSession,
        Configuration configuration,
        bool interactivePermissions,
        ISlashSessionBinding binding) => new SlashSession(client, initialSession, configuration, interactivePermissions, binding);

    public async Task SelectModel(string model, CancellationToken cancellationToken)
    {
        _current = await client.UpdateSessionAsync(
            new UpdateSessionRequest { UserSessionId = Id, Model = model },
            cancellationToken: cancellationToken);
        configuration.SetModel(_current.Model);
    }

    public async Task<ModelPreset> SetModelPreset(string name, CancellationToken cancellationToken)
    {
        var response = await client.SetModelPresetAsync(
            new SetModelPresetRequest { UserSessionId = Id, Name = name },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return response.Preset;
    }

    public async Task<ModelPreset> SelectModelPreset(string name, CancellationToken cancellationToken)
    {
        var response = await client.SelectModelPresetAsync(
            new SelectModelPresetRequest { UserSessionId = Id, Name = name },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        _current = response.Session;
        return response.Preset;
    }

    public async Task<IReadOnlyList<ModelPreset>> ListModelPresets(CancellationToken cancellationToken)
    {
        var listed = await client.ListModelPresetsAsync(
            new ListModelPresetsRequest(),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return listed.Presets;
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

    public Task Compact(string? targetContextSize, CancellationToken cancellationToken) =>
        client.CompactAsync(
            new CompactRequest
            {
                UserSessionId = Id,
                TargetContextSize = targetContextSize ?? string.Empty,
            },
            cancellationToken: cancellationToken).ResponseAsync;

    public Task<SetContextLimitResponse> SetContextLimit(string contextLimit, CancellationToken cancellationToken) =>
        client.SetContextLimitAsync(
            new SetContextLimitRequest { UserSessionId = Id, ContextLimit = contextLimit },
            cancellationToken: cancellationToken).ResponseAsync;

    public Task<SandboxEnableResponse> SandboxEnable(bool enabled, CancellationToken cancellationToken) =>
        client.SandboxEnableAsync(
            new SandboxEnableRequest { UserSessionId = Id, Enabled = enabled },
            cancellationToken: cancellationToken).ResponseAsync;

    public Task<ListSkillsResponse> ListSkills(CancellationToken cancellationToken) =>
        client.ListSkillsAsync(
            new ListSkillsRequest { UserSessionId = Id },
            cancellationToken: cancellationToken).ResponseAsync;

    public async Task<ProtocolSkill> ConfigureSkill(string path, bool enabled, CancellationToken cancellationToken)
    {
        var configured = await client.ConfigureSkillAsync(
            new ConfigureSkillRequest { UserSessionId = Id, Path = path, Enabled = enabled },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return configured.Skill;
    }

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
