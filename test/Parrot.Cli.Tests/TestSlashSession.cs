using Parrot.Cli.Commands;
using Parrot.Protocol;
using ProtocolSkill = Parrot.Protocol.Skill;

namespace Parrot.Cli.Tests;

internal sealed class TestSlashSession(string model) : ISlashSession
{
    public string Id { get; private set; } = "session-0";

    public string Model { get; private set; } = model;

    public string Mode { get; private set; } = "build";

    public List<string> Goals { get; } = [];

    public int ClearedGoals { get; private set; }

    public int Compactions { get; private set; }

    public CancellationToken CompactionCancellationToken { get; private set; }

    public ListSkillsResponse Skills { get; } = new();

    public List<ConfigureSkillRequest> ConfiguredSkills { get; } = [];

    public List<string> SetModelPresets { get; } = [];

    public List<string> SelectedModelPresets { get; } = [];

    public ModelPreset Preset { get; set; } = new() { Name = "work", Model = "provider/model" };

    public List<ModelPreset> ModelPresets { get; set; } = [];

    public int ListedModelPresets { get; private set; }

    public Task SelectModel(string model, CancellationToken cancellationToken)
    {
        Model = model;
        return Task.CompletedTask;
    }

    public Task<ModelPreset> SetModelPreset(string name, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SetModelPresets.Add(name);
        return Task.FromResult(Preset.Clone());
    }

    public Task<ModelPreset> SelectModelPreset(string name, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SelectedModelPresets.Add(name);
        var picked = ModelPresets.FirstOrDefault(preset => preset.Name == name);
        Model = picked?.Model ?? Preset.Model;
        return Task.FromResult(picked?.Clone() ?? Preset.Clone());
    }

    public Task<IReadOnlyList<ModelPreset>> ListModelPresets(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ListedModelPresets++;
        return Task.FromResult<IReadOnlyList<ModelPreset>>(ModelPresets);
    }

    public Task SelectMode(string mode, CancellationToken cancellationToken)
    {
        Mode = mode;
        return Task.CompletedTask;
    }

    public Task StartNew(string model, string mode, CancellationToken cancellationToken)
    {
        Id = "session-1";
        Model = model;
        Mode = mode;
        return Task.CompletedTask;
    }

    public Task SetGoal(string goal, CancellationToken cancellationToken)
    {
        Goals.Add(goal);
        return Task.CompletedTask;
    }

    public Task ClearGoal(CancellationToken cancellationToken)
    {
        ClearedGoals++;
        return Task.CompletedTask;
    }

    public Task Compact(CancellationToken cancellationToken)
    {
        Compactions++;
        CompactionCancellationToken = cancellationToken;
        return Task.CompletedTask;
    }

    public Task<ListSkillsResponse> ListSkills(CancellationToken cancellationToken) =>
        Task.FromResult(Skills.Clone());

    public Task<ProtocolSkill> ConfigureSkill(string path, bool enabled, CancellationToken cancellationToken)
    {
        ConfiguredSkills.Add(new ConfigureSkillRequest { UserSessionId = Id, Path = path, Enabled = enabled });
        var skill = Skills.Skills.Single(candidate => string.Equals(candidate.Path, path, StringComparison.Ordinal));
        skill.Enabled = enabled;
        return Task.FromResult(skill.Clone());
    }
}
