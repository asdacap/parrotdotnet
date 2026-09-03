using Parrot.Agent;
using Parrot.Context;
using Parrot.Process;
using Parrot.Questions;
using Parrot.Tools;
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
            .Bind<ExecCommandToolFactory>().As(Lifetime.Scoped).To(ctx =>
            {
                ctx.Inject<AgentSessionScopeArguments>(out var arguments);
                ctx.Inject<ShellProcessOwner>(out var processes);
                return new ExecCommandToolFactory(processes, arguments.ReadOnlyExecCommandPrefixes);
            })
            .Bind<WriteStdinToolFactory>().As(Lifetime.Scoped).To(ctx =>
            {
                ctx.Inject<ShellProcessOwner>(out var processes);
                return new WriteStdinToolFactory(processes);
            })
            .Bind<InterruptProcessToolFactory>().As(Lifetime.Scoped).To(ctx =>
            {
                ctx.Inject<ShellProcessOwner>(out var processes);
                return new InterruptProcessToolFactory(processes);
            })
            .Bind<QuestionToolFactory>().As(Lifetime.Scoped).To(ctx =>
            {
                ctx.Inject<AgentSessionScopeArguments>(out var arguments);
                return new QuestionToolFactory(arguments.UserQuestions, arguments.ParentScope);
            })
            .Bind<AnswerToolFactory>().As(Lifetime.Scoped).To(ctx =>
            {
                ctx.Inject<ChildQuestionCoordinator>(out var childQuestions);
                return new AnswerToolFactory(childQuestions);
            })
            .Bind<ReadToolFactory>().As(Lifetime.Scoped).To(ctx =>
            {
                ctx.Inject<AgentSessionScopeArguments>(out var arguments);
                return new ReadToolFactory(arguments.Workspace);
            })
            .Bind<ReadImageToolFactory>().As(Lifetime.Scoped).To(ctx =>
            {
                ctx.Inject<AgentSessionScopeArguments>(out var arguments);
                return new ReadImageToolFactory(arguments.Workspace, arguments.Images);
            })
            .Bind<GlobToolFactory>().As(Lifetime.Scoped).To(ctx =>
            {
                ctx.Inject<AgentSessionScopeArguments>(out var arguments);
                return new GlobToolFactory(arguments.Workspace);
            })
            .Bind<WriteToolFactory>().As(Lifetime.Scoped).To(ctx =>
            {
                ctx.Inject<AgentSessionScopeArguments>(out var arguments);
                return new WriteToolFactory(arguments.Workspace);
            })
            .Bind<EditToolFactory>().As(Lifetime.Scoped).To(ctx =>
            {
                ctx.Inject<AgentSessionScopeArguments>(out var arguments);
                return new EditToolFactory(arguments.Workspace);
            })
            .Bind<WebFetchToolFactory>().As(Lifetime.Scoped).To(ctx =>
            {
                ctx.Inject<AgentSessionScopeArguments>(out var arguments);
                return new WebFetchToolFactory(arguments.WebFetcher);
            })
            .Bind<AgentSpawnToolFactory>().As(Lifetime.Scoped).To(ctx =>
            {
                ctx.Inject<AgentSessionScopeArguments>(out var arguments);
                ctx.Inject<ChildRegistry>(out var children);
                return new AgentSpawnToolFactory(children, arguments.Router);
            })
            .Bind<RunAgentTasksToolFactory>().As(Lifetime.Scoped).To(ctx =>
            {
                ctx.Inject<AgentSessionScopeArguments>(out var arguments);
                return new RunAgentTasksToolFactory(
                    arguments.Workspace,
                    arguments.Router,
                    arguments.EventBroker,
                    arguments.EventRepository,
                    arguments.AgentTasks);
            })
            .Bind<SetCheckpointToolFactory>().As(Lifetime.Scoped).To(_ => new SetCheckpointToolFactory())
            .Bind<AgentSendToolFactory>().As(Lifetime.Scoped).To(ctx =>
            {
                ctx.Inject<AgentSessionScopeArguments>(out var arguments);
                ctx.Inject<AgentResolver>(out var resolver);
                return new AgentSendToolFactory(arguments.Identity, resolver);
            })
            .Bind<AgentStatusToolFactory>().As(Lifetime.Scoped).To(ctx =>
            {
                ctx.Inject<AgentResolver>(out var resolver);
                ctx.Inject<ChildRegistry>(out var children);
                ctx.Inject<AgentSessionScopeArguments>(out var arguments);
                return new AgentStatusToolFactory(resolver, children, arguments.ShellProcesses);
            })
            .Bind<WaitToolFactory>().As(Lifetime.Scoped).To(ctx =>
            {
                ctx.Inject<AgentSessionScopeArguments>(out var arguments);
                return new WaitToolFactory(arguments.Status, arguments.TimeProvider);
            })
            .Bind<StatusToolFactory>().As(Lifetime.Scoped).To(ctx =>
            {
                ctx.Inject<AgentSessionScopeArguments>(out var arguments);
                return new StatusToolFactory(arguments.Status);
            })
            .Bind<QueueCreateToolFactory>().As(Lifetime.Scoped).To(ctx =>
            {
                ctx.Inject<AgentSessionScopeArguments>(out var arguments);
                return new QueueCreateToolFactory(arguments.Queues);
            })
            .Bind<QueueInfoToolFactory>().As(Lifetime.Scoped).To(ctx =>
            {
                ctx.Inject<AgentSessionScopeArguments>(out var arguments);
                return new QueueInfoToolFactory(arguments.Queues);
            })
            .Bind<QueueListenToolFactory>().As(Lifetime.Scoped).To(ctx =>
            {
                ctx.Inject<AgentSessionScopeArguments>(out var arguments);
                return new QueueListenToolFactory(arguments.Queues);
            })
            .Bind<QueuePushToolFactory>().As(Lifetime.Scoped).To(ctx =>
            {
                ctx.Inject<AgentSessionScopeArguments>(out var arguments);
                return new QueuePushToolFactory(arguments.Queues, arguments.Workspace);
            })
            .Bind<QueueTakeToolFactory>().As(Lifetime.Scoped).To(ctx =>
            {
                ctx.Inject<AgentSessionScopeArguments>(out var arguments);
                return new QueueTakeToolFactory(arguments.Queues);
            })
            .Bind<RequestWritePermissionToolFactory>().As(Lifetime.Scoped).To(ctx =>
            {
                ctx.Inject<AgentSessionScopeArguments>(out var arguments);
                return new RequestWritePermissionToolFactory(arguments.Identity, arguments.Security, arguments.Permissions);
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
