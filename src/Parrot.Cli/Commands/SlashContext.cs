using Parrot.Auth;
using Parrot.Config;
using Parrot.Protocol;
using GeneratedParrot = Parrot.Protocol.Parrot;

namespace Parrot.Cli.Commands;

// What a command is allowed to touch. The session id is settable because
// /clear replaces the session the loop is talking to.
internal sealed class SlashContext(
    GeneratedParrot.ParrotClient client,
    ICredentialStore credentials,
    OpenAiOAuthClient oauth,
    Configuration configuration,
    IReadOnlyList<string> providerIds,
    string userSessionId,
    string model,
    string mode,
    TextReader input,
    TextWriter output,
    TextWriter error)
{
    public GeneratedParrot.ParrotClient Client { get; } = client;

    public ICredentialStore Credentials { get; } = credentials;

    public OpenAiOAuthClient OAuth { get; } = oauth;

    public Configuration Configuration { get; } = configuration;

    public IReadOnlyList<string> ProviderIds { get; } = providerIds;

    public string UserSessionId { get; set; } = userSessionId;

    public string Model { get; set; } = model;

    public string Mode { get; set; } = mode;

    public TextReader Input { get; } = input;

    public TextWriter Output { get; } = output;

    public TextWriter Error { get; } = error;

    public static SlashContext Create(
        GeneratedParrot.ParrotClient client,
        ICredentialStore credentials,
        OpenAiOAuthClient oauth,
        Configuration configuration,
        IReadOnlyList<string> providerIds,
        UserSession session,
        TextReader input,
        TextWriter output,
        TextWriter error) =>
        new(
            client,
            credentials,
            oauth,
            configuration,
            providerIds,
            session.Id,
            session.Model,
            session.Mode,
            input,
            output,
            error);
}
