using Parrot.Protocol;
using ProtocolSkill = Parrot.Protocol.Skill;

namespace Parrot.Cli.Commands;

internal interface ISlashSession
{
    string Id { get; }

    string Model { get; }

    string Mode { get; }

    Task SelectModel(string model, CancellationToken cancellationToken);

    Task<ModelPreset> SetModelPreset(string name, CancellationToken cancellationToken);

    Task<ModelPreset> SelectModelPreset(string name, CancellationToken cancellationToken);

    Task SelectMode(string mode, CancellationToken cancellationToken);

    Task StartNew(string model, string mode, CancellationToken cancellationToken);

    Task SetGoal(string goal, CancellationToken cancellationToken);

    Task ClearGoal(CancellationToken cancellationToken);

    Task Compact(CancellationToken cancellationToken);

    Task<ListSkillsResponse> ListSkills(CancellationToken cancellationToken);

    Task<ProtocolSkill> ConfigureSkill(string path, bool enabled, CancellationToken cancellationToken);
}
