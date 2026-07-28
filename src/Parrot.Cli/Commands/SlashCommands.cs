using Parrot.Auth;
using Parrot.Store;
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
        OpenAiOAuthClient oauth,
        IReadOnlyList<string> providerIds,
        SessionIndex sessionIndex)
    {
        var commands = new List<ISlashCommand>();
        var registry = new SlashCommandRegistry(commands, dialog);
        var models = new ModelWizard(client, dialog);
        var modes = new ModeSelection(client, dialog);
        commands.Add(new AuthCommand(credentials, oauth, providerIds, dialog));
        commands.Add(new ClearCommand(models, modes, session, activity, dialog));
        commands.Add(new EffortCommand(client, session, activity, dialog));
        commands.Add(new ExitCommand(applicationExit));
        commands.Add(new HelpCommand(registry, dialog));
        commands.Add(new ModeCommand(modes, session, activity, dialog));
        commands.Add(new ModelCommand(models, session, activity, dialog));
        commands.Add(new ModelAliasCommand(client, models, dialog));
        commands.Add(new ModelsCommand(client, dialog));
        commands.Add(new ModesCommand(client, dialog));
        commands.Add(new SessionsCommand(sessionIndex, session, dialog));
        commands.Add(new VersionCommand(dialog));
        return registry;
    }
}
