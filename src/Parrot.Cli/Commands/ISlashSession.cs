using Parrot.Protocol;
using ProtocolSkill = Parrot.Protocol.Skill;

namespace Parrot.Cli.Commands;

/// <summary>Exposes the current CLI session and routes slash-command operations to it.</summary>
internal interface ISlashSession
{
    string Id { get; }

    string Model { get; }

    string Mode { get; }

    /// <summary>Updates the current session model and the configured default model.</summary>
    Task SelectModel(string model, CancellationToken cancellationToken);

    /// <summary>Saves the current model selection under the supplied preset name.</summary>
    Task<ModelPreset> SetModelPreset(string name, CancellationToken cancellationToken);

    /// <summary>Applies a named preset to the current session and returns the selected preset.</summary>
    Task<ModelPreset> SelectModelPreset(string name, CancellationToken cancellationToken);

    Task<IReadOnlyList<ModelPreset>> ListModelPresets(CancellationToken cancellationToken);

    /// <summary>Updates the current session mode.</summary>
    Task SelectMode(string mode, CancellationToken cancellationToken);

    /// <summary>Creates a session and replaces the CLI binding, restoring the current session if binding fails.</summary>
    Task StartNew(string model, string mode, CancellationToken cancellationToken);

    Task SetGoal(string goal, CancellationToken cancellationToken);

    Task ClearGoal(CancellationToken cancellationToken);

    /// <summary>Requests compaction of the current session context.</summary>
    Task Compact(string? targetContextSize, CancellationToken cancellationToken);

    /// <summary>Persists the default context compaction trigger and returns whether an alias masks it.</summary>
    Task<SetContextLimitResponse> SetContextLimit(string contextLimit, CancellationToken cancellationToken);

    Task<ListSkillsResponse> ListSkills(CancellationToken cancellationToken);

    /// <summary>Sets whether the skill at the supplied path is enabled and returns its updated description.</summary>
    Task<ProtocolSkill> ConfigureSkill(string path, bool enabled, CancellationToken cancellationToken);
}
