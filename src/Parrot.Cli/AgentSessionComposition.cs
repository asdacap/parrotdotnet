using Parrot.Agent;
using Parrot.Config;
using Parrot.Context;
using Parrot.Events;
using Parrot.Llm;
using Parrot.Permissions;
using Parrot.Process;
using Parrot.Questions;
using Parrot.Queues;
using Parrot.Statuses;
using Parrot.Store;
using Parrot.Tools;
using Parrot.Web;
using Pure.DI;

namespace Parrot.Cli;

internal partial class AgentSessionComposition
{
    internal static void Setup() =>
        DI.Setup(nameof(AgentSessionComposition))
            .Hint(Hint.Resolve, "Off")
            .TagAttribute<InjectionTagAttribute>()
            .Arg<AgentSessionScopeArguments>("arguments")
            .Bind().As(Lifetime.Scoped).To<ISystemPrompt>(ctx =>
            {
                ctx.Inject<AgentSessionScopeArguments>(out var arguments);
                return arguments.SystemPromptProvider.Materialize(arguments.Identity)
                    ?? throw new InvalidOperationException("The system prompt provider returned no prompt.");
            })
            .Bind().As(Lifetime.Scoped).To(ctx =>
            {
                ctx.Inject<AgentSessionScopeArguments>(out var arguments);
                return arguments.ShellProcesses.Prepare(arguments.Identity.SessionId);
            })
            .Bind<IAgentSessionScope>().To(ctx =>
            {
                ctx.Inject<AgentSessionScopeArguments>(out var arguments);
                return arguments.Scope;
            })
            .Bind<AgentIdentity>().To(ctx =>
            {
                ctx.Inject<AgentSessionScopeArguments>(out var arguments);
                return arguments.Identity;
            })
            .Bind<AgentSessionParentScope>().To(ctx =>
            {
                ctx.Inject<AgentSessionScopeArguments>(out var arguments);
                return arguments.ParentScope;
            })
            .Bind<ModelSelector>().To(ctx =>
            {
                ctx.Inject<AgentSessionScopeArguments>(out var arguments);
                return arguments.Model;
            })
            .Bind<ModelRouter>().To(ctx =>
            {
                ctx.Inject<AgentSessionScopeArguments>(out var arguments);
                return arguments.Router;
            })
            .Bind<EventBroker>().To(ctx =>
            {
                ctx.Inject<AgentSessionScopeArguments>(out var arguments);
                return arguments.EventBroker;
            })
            .Bind<EventRepository>().To(ctx =>
            {
                ctx.Inject<AgentSessionScopeArguments>(out var arguments);
                return arguments.EventRepository;
            })
            .Bind<ToolDefinitionCatalog>().To(ctx =>
            {
                ctx.Inject<AgentSessionScopeArguments>(out var arguments);
                return arguments.ToolDefinitions;
            })
            .Bind<ToolWorkspace>().To(ctx =>
            {
                ctx.Inject<AgentSessionScopeArguments>(out var arguments);
                return arguments.Workspace;
            })
            .Bind<ImageArtifactRepository>().To(ctx =>
            {
                ctx.Inject<AgentSessionScopeArguments>(out var arguments);
                return arguments.Images;
            })
            .Bind<WebFetcher>().To(ctx =>
            {
                ctx.Inject<AgentSessionScopeArguments>(out var arguments);
                return arguments.WebFetcher;
            })
            .Bind<AgentTaskConfig>().To(ctx =>
            {
                ctx.Inject<AgentSessionScopeArguments>(out var arguments);
                return arguments.AgentTasks;
            })
            .Bind<IReadOnlyList<string>>().To(ctx =>
            {
                ctx.Inject<AgentSessionScopeArguments>(out var arguments);
                return arguments.ReadOnlyExecCommandPrefixes;
            })
            .Bind<ShellProcessOwners>().To(ctx =>
            {
                ctx.Inject<AgentSessionScopeArguments>(out var arguments);
                return arguments.ShellProcesses;
            })
            .Bind<QuestionBroker>().To(ctx =>
            {
                ctx.Inject<AgentSessionScopeArguments>(out var arguments);
                return arguments.UserQuestions;
            })
            .Bind<CompactionGroupBlobStore>().As(Lifetime.Scoped).To(ctx =>
            {
                ctx.Inject<AgentSessionScopeArguments>(out var arguments);
                return new CompactionGroupBlobStore(arguments.Scratch);
            })
            .Bind<Compactor>().To(ctx =>
            {
                ctx.Inject<AgentSessionScopeArguments>(out var arguments);
                return arguments.Compactor;
            })
            .Bind<PromptTemplateCatalog>().To(ctx =>
            {
                ctx.Inject<AgentSessionScopeArguments>(out var arguments);
                return arguments.PromptTemplates;
            })
            .Bind<IMode>().To(ctx =>
            {
                ctx.Inject<AgentSessionScopeArguments>(out var arguments);
                return arguments.Mode;
            })
            .Bind<RuntimeStatus>().To(ctx =>
            {
                ctx.Inject<AgentSessionScopeArguments>(out var arguments);
                return arguments.Status;
            })
            .Bind<TimeProvider>().To(ctx =>
            {
                ctx.Inject<AgentSessionScopeArguments>(out var arguments);
                return arguments.TimeProvider;
            })
            .Bind<AgentSessionSecurity>().To(ctx =>
            {
                ctx.Inject<AgentSessionScopeArguments>(out var arguments);
                return arguments.Security;
            })
            .Bind<ContextCadence>().As(Lifetime.Scoped).To<ContextCadence>()
            .Bind<PermissionBroker>().To(ctx =>
            {
                ctx.Inject<AgentSessionScopeArguments>(out var arguments);
                return arguments.Permissions;
            })
            .Bind<AgentQueues>().To(ctx =>
            {
                ctx.Inject<AgentSessionScopeArguments>(out var arguments);
                return arguments.Queues;
            })
            .Bind<ExecCommandToolFactory>().As(Lifetime.Scoped).To<ExecCommandToolFactory>()
            .Bind<WriteStdinToolFactory>().As(Lifetime.Scoped).To<WriteStdinToolFactory>()
            .Bind<InterruptProcessToolFactory>().As(Lifetime.Scoped).To<InterruptProcessToolFactory>()
            .Bind<QuestionToolFactory>().As(Lifetime.Scoped).To<QuestionToolFactory>()
            .Bind<AnswerToolFactory>().As(Lifetime.Scoped).To<AnswerToolFactory>()
            .Bind<ReadToolFactory>().As(Lifetime.Scoped).To<ReadToolFactory>()
            .Bind<ReadImageToolFactory>().As(Lifetime.Scoped).To<ReadImageToolFactory>()
            .Bind<GlobToolFactory>().As(Lifetime.Scoped).To<GlobToolFactory>()
            .Bind<WriteToolFactory>().As(Lifetime.Scoped).To<WriteToolFactory>()
            .Bind<EditToolFactory>().As(Lifetime.Scoped).To<EditToolFactory>()
            .Bind<WebFetchToolFactory>().As(Lifetime.Scoped).To<WebFetchToolFactory>()
            .Bind<AgentSpawnToolFactory>().As(Lifetime.Scoped).To<AgentSpawnToolFactory>()
            .Bind<RunAgentTasksToolFactory>().As(Lifetime.Scoped).To<RunAgentTasksToolFactory>()
            .Bind<SetCheckpointToolFactory>().As(Lifetime.Scoped).To<SetCheckpointToolFactory>()
            .Bind<SetExitReminderToolFactory>().As(Lifetime.Scoped).To(ctx =>
            {
                ctx.Inject<AgentSessionScopeArguments>(out var arguments);
                ctx.Inject<ExitReminder>(out var reminder);
                return new SetExitReminderToolFactory(reminder, arguments.PromptTemplates);
            })
            .Bind<AgentSendToolFactory>().As(Lifetime.Scoped).To<AgentSendToolFactory>()
            .Bind<AgentStatusToolFactory>().As(Lifetime.Scoped).To<AgentStatusToolFactory>()
            .Bind<WaitToolFactory>().As(Lifetime.Scoped).To<WaitToolFactory>()
            .Bind<StatusToolFactory>().As(Lifetime.Scoped).To<StatusToolFactory>()
            .Bind<CompactContextToolFactory>().As(Lifetime.Scoped).To<CompactContextToolFactory>()
            .Bind<QueueCreateToolFactory>().As(Lifetime.Scoped).To<QueueCreateToolFactory>()
            .Bind<QueueInfoToolFactory>().As(Lifetime.Scoped).To<QueueInfoToolFactory>()
            .Bind<QueueListenToolFactory>().As(Lifetime.Scoped).To<QueueListenToolFactory>()
            .Bind<QueuePushToolFactory>().As(Lifetime.Scoped).To<QueuePushToolFactory>()
            .Bind<QueueTakeToolFactory>().As(Lifetime.Scoped).To<QueueTakeToolFactory>()
            .Bind<RequestWritePermissionToolFactory>().As(Lifetime.Scoped).To<RequestWritePermissionToolFactory>()
            .Bind<ExitReminder>().As(Lifetime.Scoped).To(ctx =>
            {
                ctx.Inject<AgentSessionScopeArguments>(out var arguments);
                return new ExitReminder(arguments.EventRepository, arguments.PromptTemplates, arguments.Identity.SessionId);
            })
            .Bind<PendingChildQuestionTurnCompletionCallback>().As(Lifetime.Scoped).To<PendingChildQuestionTurnCompletionCallback>()
            .Bind<ActiveWorkTurnCompletionCallback>().As(Lifetime.Scoped).To<ActiveWorkTurnCompletionCallback>()
            .Bind<ModeTurnCompletionCallback>().As(Lifetime.Scoped).To<ModeTurnCompletionCallback>()
            .Bind<ExitReminderTurnCompletionCallback>().As(Lifetime.Scoped).To<ExitReminderTurnCompletionCallback>()
            .Bind<IReadOnlyList<IAgentTurnCompletionCallback>>("turnCompletionCallbacks").As(Lifetime.Scoped).To(ctx =>
            {
                ctx.Inject<PendingChildQuestionTurnCompletionCallback>(out var pendingChildQuestions);
                ctx.Inject<ActiveWorkTurnCompletionCallback>(out var activeWork);
                ctx.Inject<ModeTurnCompletionCallback>(out var modeCompletion);
                ctx.Inject<ExitReminderTurnCompletionCallback>(out var exitReminder);
                return new IAgentTurnCompletionCallback[]
                {
                    pendingChildQuestions,
                    activeWork,
                    modeCompletion,
                    exitReminder,
                };
            })
            .Bind<IReadOnlyList<IToolFactory>>("toolFactories").As(Lifetime.Scoped).To(ctx =>
            {
                ctx.Inject<ExecCommandToolFactory>(out var execCommand);
                ctx.Inject<WriteStdinToolFactory>(out var writeStdin);
                ctx.Inject<InterruptProcessToolFactory>(out var interruptProcess);
                ctx.Inject<QuestionToolFactory>(out var question);
                ctx.Inject<AnswerToolFactory>(out var answer);
                ctx.Inject<ReadToolFactory>(out var read);
                ctx.Inject<ReadImageToolFactory>(out var readImage);
                ctx.Inject<GlobToolFactory>(out var glob);
                ctx.Inject<WriteToolFactory>(out var write);
                ctx.Inject<EditToolFactory>(out var edit);
                ctx.Inject<WebFetchToolFactory>(out var webFetch);
                ctx.Inject<AgentSpawnToolFactory>(out var agentSpawn);
                ctx.Inject<RunAgentTasksToolFactory>(out var runAgentTasks);
                ctx.Inject<SetCheckpointToolFactory>(out var setCheckpoint);
                ctx.Inject<SetExitReminderToolFactory>(out var setExitReminder);
                ctx.Inject<AgentSendToolFactory>(out var agentSend);
                ctx.Inject<AgentStatusToolFactory>(out var agentStatus);
                ctx.Inject<WaitToolFactory>(out var wait);
                ctx.Inject<StatusToolFactory>(out var status);
                ctx.Inject<CompactContextToolFactory>(out var compactContext);
                ctx.Inject<QueueCreateToolFactory>(out var queueCreate);
                ctx.Inject<QueueInfoToolFactory>(out var queueInfo);
                ctx.Inject<QueueListenToolFactory>(out var queueListen);
                ctx.Inject<QueuePushToolFactory>(out var queuePush);
                ctx.Inject<QueueTakeToolFactory>(out var queueTake);
                ctx.Inject<RequestWritePermissionToolFactory>(out var requestWritePermission);
                return new IToolFactory[]
                {
                    execCommand,
                    writeStdin,
                    interruptProcess,
                    question,
                    answer,
                    read,
                    readImage,
                    glob,
                    write,
                    edit,
                    webFetch,
                    agentSpawn,
                    runAgentTasks,
                    setCheckpoint,
                    setExitReminder,
                    agentSend,
                    agentStatus,
                    wait,
                    status,
                    compactContext,
                    queueCreate,
                    queueInfo,
                    queueListen,
                    queuePush,
                    queueTake,
                    requestWritePermission,
                };
            })
            .Bind().As(Lifetime.Scoped).To(ctx =>
            {
                ctx.Inject<AgentSessionScopeArguments>(out var arguments);
                ctx.Inject<ShellProcessOwner>(out var processes);
                ctx.Inject<IChildRegistry>(out var children);
                return new ActiveWorkCompletionReminder(
                    children,
                    processes,
                    arguments.PromptTemplates);
            })
            .Bind().As(Lifetime.Scoped).To(ctx =>
            {
                ctx.Inject<AgentSessionScopeArguments>(out var arguments);
                return new ToolOutputBlobStore(arguments.Scratch.BlobDirectory);
            })
            .Bind().As(Lifetime.Scoped).To(ctx =>
            {
                ctx.Inject<AgentSessionScopeArguments>(out var arguments);
                return new AgentSessionActivity(arguments.TimeProvider);
            })
            .Bind<IChildRegistry>().To(ctx =>
            {
                ctx.Inject<IAgentSessionScope>(out var scope);
                return scope.ChildRegistry;
            })
            .Bind<ChildQuestionCoordinator>().To(ctx =>
            {
                ctx.Inject<IAgentSessionScope>(out var scope);
                return scope.ChildQuestions;
            })
            .Bind().As(Lifetime.Scoped).To(ctx =>
            {
                ctx.Inject<AgentSessionScopeArguments>(out var arguments);
                return new AgentResolver(arguments.Identity, arguments.ParentScope, arguments.Scope, arguments.Registry);
            })
            .Bind<IAgentSession>().As(Lifetime.PerResolve).To<AgentSession>()
            .Root<IAgentSession>("Session")
            .Root<ShellProcessOwner>("Processes");
}
