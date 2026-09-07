using Parrot.Auth;
using GeneratedParrot = Parrot.Protocol.Parrot;

namespace Parrot.Cli.Commands;

internal static class SlashCommands
{
    public static SlashCommandRegistry Create(
        GeneratedParrot.ParrotClient client,
        ISlashDialog dialog,
        ISlashSession session,
        ISlashActivity activity,
        IApplicationExit applicationExit,
        ICredentialStore credentials,
        IOAuthClient oauth,
        IReadOnlyList<string> providerIds,
        Func<CancellationToken, Task> refreshSkillCompletion)
    {
        var commands = new List<ISlashCommand>();
        var registry = new SlashCommandRegistry(commands, dialog);
        var models = new ModelWizard(client, dialog);
        var modes = new ModeSelection(client, dialog);
        commands.Add(new AuthCommand(credentials, oauth, providerIds, dialog));
        commands.Add(new ClearCommand(models, modes, session, activity, dialog));
        commands.Add(new CompactCommand(session, activity, dialog));
        commands.Add(new EffortCommand(client, session, activity, dialog));
        commands.Add(new ExitCommand(applicationExit));
        commands.Add(new GoalCommand(session, dialog));
        commands.Add(new HelpCommand(registry, dialog));
        commands.Add(new ModeCommand(modes, session, activity, dialog, refreshSkillCompletion));
        commands.Add(new ModelCommand(models, session, activity, dialog));
        commands.Add(new ModelAliasCommand(client, models, dialog));
        commands.Add(new ModelPresetSelectCommand(session, activity, dialog));
        commands.Add(new ModelPresetSetCommand(session, dialog));
        commands.Add(new ModelsCommand(client, dialog));
        commands.Add(new ModesCommand(client, dialog));
        commands.Add(new SessionsCommand(client, session, dialog));
        commands.Add(new SkillsCommand(session, activity, dialog, refreshSkillCompletion));
        commands.Add(new VersionCommand(dialog));
        return registry;
    }
}
