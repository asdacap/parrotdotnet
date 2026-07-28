using Parrot.Auth;
using Parrot.Config;
using Pure.DI;
using GeneratedParrot = Parrot.Protocol.Parrot;

namespace Parrot.Cli.Enhanced;

internal partial class EnhancedComposition
{
    internal static void Setup() =>
        DI.Setup(nameof(EnhancedComposition))
            .Hint(Hint.Resolve, "Off")
            .Arg<GeneratedParrot.ParrotClient>("client")
            .Arg<Interrupts>("interrupts")
            .Arg<ICredentialStore>("credentials")
            .Arg<OpenAiOAuthClient>("oauthClient")
            .Arg<Configuration>("configuration")
            .Arg<IReadOnlyList<string>>("providerIds")
            .Arg<EnhancedChatRequest>("request")
            .Arg<ITerminal>("terminal")
            .Root<EnhancedCli>("Cli");
}
