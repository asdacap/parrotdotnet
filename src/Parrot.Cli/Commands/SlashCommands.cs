using Parrot.Auth;
using Parrot.Diagnostics;
using GeneratedParrot = Parrot.Protocol.Parrot;

namespace Parrot.Cli.Commands;

internal static class SlashCommands
{
    public static SlashCommandRegistry Create(
        GeneratedParrot.ParrotClient client,
        ISlashDialog dialog,
        ISlashSession session,
        ISlashActivity activity,
        IApplicationExit? applicationExit,
        ICredentialStore credentials,
        CredentialPresets credentialPresets,
        IOAuthClient oauth,
        IReadOnlyList<string> providerIds,
        Func<CancellationToken, Task> refreshSkillCompletion,
        IDiagnosticLog diagnostics)
        => CreateCore(client, dialog, session, activity, applicationExit, credentials, credentialPresets, oauth, providerIds, refreshSkillCompletion, diagnostics, []);

    public static SlashCommandRegistry CreateTerminal(
        GeneratedParrot.ParrotClient client,
        ISlashDialog dialog,
        ISlashSession session,
        ISlashActivity activity,
        IApplicationExit? applicationExit,
        ICredentialStore credentials,
        CredentialPresets credentialPresets,
        IOAuthClient oauth,
        IReadOnlyList<string> providerIds,
        Func<CancellationToken, Task> refreshSkillCompletion,
        IDiagnosticLog diagnostics,
        ITerminalSessionController navigation)
        => CreateCore(
            client,
            dialog,
            session,
            activity,
            applicationExit,
            credentials,
            credentialPresets,
            oauth,
            providerIds,
            refreshSkillCompletion,
            diagnostics,
            [new WorkspaceSessionsCommand(navigation, session, activity, dialog)]);

    private static SlashCommandRegistry CreateCore(
        GeneratedParrot.ParrotClient client,
        ISlashDialog dialog,
        ISlashSession session,
        ISlashActivity activity,
        IApplicationExit? applicationExit,
        ICredentialStore credentials,
        CredentialPresets credentialPresets,
        IOAuthClient oauth,
        IReadOnlyList<string> providerIds,
        Func<CancellationToken, Task> refreshSkillCompletion,
        IDiagnosticLog diagnostics,
        IReadOnlyList<ISlashCommand> terminalCommands)
    {
        var commands = new List<ISlashCommand>();
        var registry = new SlashCommandRegistry(commands, dialog);
        var models = new ModelWizard(client, dialog);
        var modes = new ModeSelection(client, dialog);
        commands.Add(new AuthCommand(credentials, oauth, providerIds, dialog, diagnostics));
        commands.Add(new AuthPresetSelectCommand(credentialPresets, credentials, activity, dialog));
        commands.Add(new AuthPresetSetCommand(credentialPresets, credentials, dialog));
        commands.Add(new ClearCommand(models, modes, session, activity, dialog));
        commands.Add(new CompactCommand(session, activity, dialog));
        commands.Add(new EffortCommand(client, session, activity, dialog));
        if (applicationExit is not null)
        {
            commands.Add(new ExitCommand(applicationExit));
        }

        commands.Add(new GoalCommand(session));
        commands.Add(new StandingInstructionCommand(session));
        commands.Add(new HelpCommand(registry, dialog));
        commands.Add(new ModeCommand(modes, session, activity, dialog, refreshSkillCompletion));
        commands.Add(new ModelCommand(models, session, activity, dialog));
        commands.Add(new ModelAliasCommand(client, models, dialog));
        commands.Add(new ModelPresetSelectCommand(session, activity, dialog));
        commands.Add(new ModelPresetSetCommand(session, dialog));
        commands.Add(new ModelsCommand(client, dialog));
        commands.Add(new ModesCommand(client, dialog));
        commands.Add(new SessionsCommand(client, session, dialog));
        commands.Add(new StatusCommand(client, session, dialog));
        commands.Add(new SandboxEnableCommand(session, dialog));
        commands.Add(new SetContextLimitCommand(session, dialog));
        commands.Add(new SkillsCommand(session, activity, dialog, refreshSkillCompletion));
        commands.Add(new VersionCommand(dialog));
        commands.AddRange(terminalCommands);
        return registry;
    }
}
