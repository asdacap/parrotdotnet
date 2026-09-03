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
            .Bind<AgentSendToolFactory>().As(Lifetime.Scoped).To<AgentSendToolFactory>()
            .Bind<AgentStatusToolFactory>().As(Lifetime.Scoped).To<AgentStatusToolFactory>()
            .Bind<WaitToolFactory>().As(Lifetime.Scoped).To<WaitToolFactory>()
            .Bind<StatusToolFactory>().As(Lifetime.Scoped).To<StatusToolFactory>()
            .Bind<QueueCreateToolFactory>().As(Lifetime.Scoped).To<QueueCreateToolFactory>()
            .Bind<QueueInfoToolFactory>().As(Lifetime.Scoped).To<QueueInfoToolFactory>()
            .Bind<QueueListenToolFactory>().As(Lifetime.Scoped).To<QueueListenToolFactory>()
            .Bind<QueuePushToolFactory>().As(Lifetime.Scoped).To<QueuePushToolFactory>()
            .Bind<QueueTakeToolFactory>().As(Lifetime.Scoped).To<QueueTakeToolFactory>()
            .Bind<RequestWritePermissionToolFactory>().As(Lifetime.Scoped).To<RequestWritePermissionToolFactory>()
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
                ctx.Inject<AgentSendToolFactory>(out var agentSend);
                ctx.Inject<AgentStatusToolFactory>(out var agentStatus);
                ctx.Inject<WaitToolFactory>(out var wait);
                ctx.Inject<StatusToolFactory>(out var status);
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
                    agentSend,
                    agentStatus,
                    wait,
                    status,
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
                ctx.Inject<ChildRegistry>(out var children);
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
            .Bind().As(Lifetime.Scoped).To(ctx =>
            {
                ctx.Inject<AgentSessionScopeArguments>(out var arguments);
                return new ChildRegistry(arguments.Identity, arguments.Registry);
            })
            .Bind().As(Lifetime.Scoped).To(ctx =>
            {
                ctx.Inject<AgentSessionScopeArguments>(out var arguments);
                ctx.Inject<ChildRegistry>(out var children);
                return new ChildQuestionCoordinator(children, arguments.PromptTemplates);
            })
            .Bind().As(Lifetime.Scoped).To(ctx =>
            {
                ctx.Inject<AgentSessionScopeArguments>(out var arguments);
                ctx.Inject<ChildRegistry>(out var children);
                return new AgentResolver(arguments.Identity, arguments.ParentScope, children, arguments.Registry);
            })
            .Bind().As(Lifetime.Scoped).To(ctx =>
            {
                ctx.Inject<AgentSessionScopeArguments>(out var arguments);
                ctx.Inject<ToolOutputBlobStore>(out var toolOutputBlobs);
                ctx.Inject<ShellProcessOwner>(out var processes);
                ctx.Inject<ActiveWorkCompletionReminder>(out var activeWorkReminder);
                ctx.Inject<AgentSessionActivity>(out var activity);
                ctx.Inject<ChildRegistry>(out var children);
                ctx.Inject<ChildQuestionCoordinator>(out var childQuestions);
                ctx.Inject<IReadOnlyList<IToolFactory>>("toolFactories", out var toolFactories);
                ctx.Inject<ISystemPrompt>(out var systemPrompt);
                var session = new AgentSession(
                    arguments.Identity,
                    arguments.ParentScope,
                    arguments.Model,
                    arguments.Router,
                    arguments.EventBroker,
                    arguments.EventRepository,
                    toolFactories,
                    arguments.ToolDefinitions,
                    systemPrompt,
                    toolOutputBlobs,
                    arguments.Compactor,
                    arguments.PromptTemplates,
                    childQuestions,
                    activeWorkReminder,
                    arguments.Mode,
                    arguments.Security,
                    arguments.Status,
                    children,
                    arguments.Queues,
                    activity,
                    arguments.Lifetime);
                arguments.Queues.Attach(session);
                arguments.ShellProcesses.Register(processes);
                return session;
            })
            .Root<AgentSession>("Session")
            .Root<ChildRegistry>("Children")
            .Root<ChildQuestionCoordinator>("ChildQuestions");
}
