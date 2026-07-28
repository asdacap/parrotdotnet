using Parrot.Auth;
using Parrot.Cli.Enhanced.Tools;
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
            .Bind<ToolPresenterRegistry>()
            .To(static _ => new ToolPresenterRegistry(
                [
                    new AgentSendToolPresenter(),
                    new AgentSpawnToolPresenter(),
                    new ApplyPatchToolPresenter(),
                    new ExecCommandToolPresenter(),
                    new GlobToolPresenter(),
                    new GrepToolPresenter(),
                    new InterruptProcessToolPresenter(),
                    new ReadToolPresenter(),
                    new TodoReadToolPresenter(),
                    new TodoWriteToolPresenter(),
                    new WaitAgentToolPresenter(),
                    new WaitProcessToolPresenter(),
                    new WebFetchToolPresenter(),
                ],
                new GenericToolPresenter()))
            .Root<EnhancedCli>("Cli");
}
