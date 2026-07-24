using Parrot.Auth;
using GeneratedParrot = Parrot.Protocol.Parrot;

namespace Parrot.Cli.Commands;

// What a command is allowed to touch. The session id is settable because
// /clear replaces the session the loop is talking to.
internal sealed class SlashContext(
    GeneratedParrot.ParrotClient client,
    ICredentialStore credentials,
    string providerId,
    string userSessionId,
    TextWriter output,
    TextWriter error)
{
    public GeneratedParrot.ParrotClient Client { get; } = client;

    public ICredentialStore Credentials { get; } = credentials;

    public string ProviderId { get; } = providerId;

    public string UserSessionId { get; set; } = userSessionId;

    public TextWriter Output { get; } = output;

    public TextWriter Error { get; } = error;
}
