using Parrot.Auth;
using Parrot.Cli.Commands;
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
            .Arg<SlashCommandRegistry>("commands")
            .Arg<Interrupts>("interrupts")
            .Arg<ICredentialStore>("credentials")
            .Arg<OpenAiOAuthClient>("oauthClient")
            .Arg<Configuration>("configuration")
            .Arg<IReadOnlyList<string>>("providerIds")
            .Arg<EnhancedChatRequest>("request")
            .Arg<TextWriter>("output", "output")
            .Arg<TextWriter>("error", "error")
            .Bind<ITerminal>().To(ctx =>
            {
                ctx.Inject<TextWriter>("output", out var output);
                ctx.Inject<TextWriter>("error", out var error);
                return new ConsoleTerminal(output, error);
            })
            .Root<EnhancedCli>("Cli");
}
