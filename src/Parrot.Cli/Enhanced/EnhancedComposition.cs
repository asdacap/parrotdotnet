using Parrot.Auth;
using Parrot.Cli.Enhanced.Tools;
using Parrot.Config;
using Parrot.Diagnostics;
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
            .Arg<IOAuthClient>("oauthClient")
            .Arg<Configuration>("configuration")
            .Arg<IReadOnlyList<string>>("providerIds")
            .Arg<EnhancedChatRequest>("request")
            .Arg<ITerminal>("terminal")
            .Arg<PromptAttachmentUploader>("attachments")
            .Arg<IDiagnosticLog>("diagnostics")
            .Bind<TimeProvider>().To(_ => TimeProvider.System)
            .Bind<Func<TimeSpan, CancellationToken, Task>>()
            .To<Func<TimeSpan, CancellationToken, Task>>(
                static _ => static (delay, cancellationToken) => Task.Delay(delay, cancellationToken))
            .Bind<ToolPresenterRegistry>().As(Lifetime.Singleton)
            .To(ctx =>
            {
                ctx.Inject<Configuration>(out var configuration);
                ctx.Inject<TimeProvider>(out var timeProvider);
                return new ToolPresenterRegistry(
                [
                    new AgentSendToolPresenter(),
                    new AgentSpawnToolPresenter(),
                    new EditToolPresenter(),
                    new ExecCommandToolPresenter(timeProvider, configuration.ReadOnlyExecCommandPrefixes),
                    new GlobToolPresenter(),
                    new InterruptProcessToolPresenter(),
                    new ReadToolPresenter(),
                    new RunAgentTasksToolPresenter(new GenericToolPresenter()),
                    new QuestionToolPresenter(),
                    new SetCheckpointToolPresenter(),
                    new QueuePushToolPresenter(),
                    new QueueTakeToolPresenter(),
                    new WaitToolPresenter(),
                    new WebFetchToolPresenter(),
                    new WriteStdinToolPresenter(),
                    new WriteToolPresenter(),
                ],
                new GenericToolPresenter());
            })
            .Root<EnhancedCli>("Cli");
}
