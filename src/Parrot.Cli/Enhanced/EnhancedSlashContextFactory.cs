using Parrot.Auth;
using Parrot.Cli.Commands;
using Parrot.Config;
using Parrot.Protocol;
using GeneratedParrot = Parrot.Protocol.Parrot;

namespace Parrot.Cli.Enhanced;

internal sealed class EnhancedSlashContextFactory(
    GeneratedParrot.ParrotClient client,
    ICredentialStore credentials,
    OpenAiOAuthClient oauthClient,
    Configuration configuration,
    IReadOnlyList<string> providerIds,
    ITerminal terminal)
{
    public SlashContext Create(UserSession session) =>
        new(
            client,
            credentials,
            oauthClient,
            configuration,
            providerIds,
            session.Id,
            session.Model,
            session.Mode,
            terminal.Input,
            terminal.Output,
            terminal.Error);
}
