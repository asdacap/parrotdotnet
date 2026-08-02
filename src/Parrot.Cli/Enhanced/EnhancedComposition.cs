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
            .Arg<PromptAttachmentUploader>("attachments")
            .Bind<Func<TimeSpan, CancellationToken, Task>>()
            .To<Func<TimeSpan, CancellationToken, Task>>(
                static _ => static (delay, cancellationToken) => Task.Delay(delay, cancellationToken))
            .Bind<ToolPresenterRegistry>().As(Lifetime.Singleton)
            .To(static _ => new ToolPresenterRegistry(
                [
                    new AgentSendToolPresenter(),
                    new AgentSpawnToolPresenter(),
                    new EditToolPresenter(),
                    new ExecCommandToolPresenter(),
                    new GlobToolPresenter(),
                    new GrepToolPresenter(),
                    new InterruptProcessToolPresenter(),
                    new ReadToolPresenter(),
                    new QuestionToolPresenter(),
                    new QueuePushToolPresenter(),
                    new QueueTakeToolPresenter(),
                    new TodoReadToolPresenter(),
                    new TodoWriteToolPresenter(),
                    new WaitToolPresenter(),
                    new WaitAgentToolPresenter(),
                    new WaitProcessToolPresenter(),
                    new WebFetchToolPresenter(),
                    new WriteStdinToolPresenter(),
                    new WriteToolPresenter(),
                ],
                new GenericToolPresenter()))
            .Root<EnhancedCli>("Cli");
}
