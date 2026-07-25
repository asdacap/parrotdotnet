using Parrot.Protocol;

namespace Parrot.Cli.Enhanced;

internal sealed class EnhancedChatRequest(CreateSessionRequest session, string prompt)
{
    public CreateSessionRequest Session { get; } = session;

    public string Prompt { get; } = prompt;
}
