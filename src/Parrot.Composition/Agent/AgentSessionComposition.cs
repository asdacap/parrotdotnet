using Parrot.AgentTasks;
using Parrot.Config;
using Parrot.Context;
using Parrot.Diagnostics;
using Parrot.Events;
using Parrot.Llm;
using Parrot.Permissions;
using Parrot.Process;
using Parrot.Questions;
using Parrot.Queues;
using Parrot.Skills;
using Parrot.Statuses;
using Parrot.Store;
using Parrot.Tools;
using Parrot.Web;
using Pure.DI;

namespace Parrot.Agent;

internal partial class AgentSessionComposition : IAsyncDisposable
{
    internal static void Setup() =>
        DI.Setup(nameof(AgentSessionComposition))
            .Hint(Hint.Resolve, "Off")
            .TagAttribute<InjectionTagAttribute>()
            .Arg<AgentSessionScopeArguments>("arguments")
            .Arg<IAgentSessionScope>("scope")
            .Bind().As(Lifetime.Scoped).To<ISystemPrompt>(ctx =>
            {
                ctx.Inject<AgentSessionScopeArguments>(out var arguments);
                return arguments.SystemPromptProvider.Materialize(arguments.Identity)
                    ?? throw new InvalidOperationException("The system prompt provider returned no prompt.");
            })
            .Bind<IProcessOwner>().As(Lifetime.Scoped).To(ctx =>
            {
                ctx.Inject<AgentSessionScopeArguments>(out var arguments);
                return new ShellProcessOwner(arguments.Identity, arguments.Resources, arguments.PathEnvironment, arguments.ProcessRunner, arguments.Diagnostics, arguments.Lifetime);
            })
            .Bind<AgentIdentity>().To((AgentSessionScopeArguments arguments) => arguments.Identity)
            .Bind<ChildRegistry>().As(Lifetime.Scoped).To(ctx =>
            {
                ctx.Inject<AgentSessionScopeArguments>(out var arguments);
                return new ChildRegistry(arguments.Identity, QueueChildAdmissionValidator.Validate);
            })
            .Bind<IChildRegistry>().As(Lifetime.Scoped).To(ctx =>
            {
                ctx.Inject<ChildRegistry>(out var children);
                return children;
            })
            .Bind<IAgentParentScope>().As(Lifetime.Scoped).To(ctx =>
            {
                ctx.Inject<AgentSessionScopeArguments>(out var arguments);
                ctx.Inject<IAgentSessionScope>(out var scope);
                ctx.Inject<IChildRegistry>(out var children);
                return AgentSessionParentScope.Bind(
                    arguments.Identity,
                    arguments.Registry,
                    () => scope,
                    children,
                    arguments.ParentLink);
            })
            .Bind<IAgentSpawner>().As(Lifetime.Scoped).To(ctx =>
            {
                ctx.Inject<AgentSessionScopeArguments>(out var arguments);
                ctx.Inject<IAgentParentScope>(out var parentSessionScope);
                ctx.Inject<IChildRegistry>(out var children);
                return new AgentSpawner(arguments.Identity, arguments.Registry, parentSessionScope, children);
            })
            .Bind<IChildQuestion>().As(Lifetime.Scoped).To<ChildQuestion>()
            .Bind<IChildQuestionCoordinator>().As(Lifetime.Scoped).To<ChildQuestionCoordinator>()
            .Bind<ModelSelector>().To((AgentSessionScopeArguments arguments) => arguments.Model)
            .Bind<IModelRouter>().To((AgentSessionScopeArguments arguments) => arguments.Router)
            .Bind<IEventBroker>().To((AgentSessionScopeArguments arguments) => arguments.EventBroker)
            .Bind<IUserSessionStatistics>().To((AgentSessionScopeArguments arguments) => arguments.EventRepository.GetRuntimeStatistics())
            .Bind<AgentSessionStatistics>().As(Lifetime.Scoped).To(ctx =>
            {
                ctx.Inject<AgentSessionScopeArguments>(out var arguments);
                ctx.Inject<IUserSessionStatistics>(out var userStatistics);
                return userStatistics.GetAgentStatistics(arguments.Identity.SessionId);
            })
            .Bind<IEventRepository>().To((AgentSessionScopeArguments arguments) => arguments.EventRepository)
            .Bind<ToolDefinitionCatalog>().To((AgentSessionScopeArguments arguments) => arguments.ToolDefinitions)
            .Bind<ToolWorkspace>().To((AgentSessionScopeArguments arguments) => arguments.Workspace)
            .Bind<IImageArtifactRepository>().To((AgentSessionScopeArguments arguments) => arguments.Images)
            .Bind<WebFetcher>().To((AgentSessionScopeArguments arguments) => arguments.WebFetcher)
            .Bind<RequestLimitsConfig>().To((AgentSessionScopeArguments arguments) => arguments.RequestLimits)
            .Bind<AgentSendConfig>().To((AgentSessionScopeArguments arguments) => arguments.AgentSend)
            .Bind<IAgentTaskService>().As(Lifetime.Scoped).To(ctx =>
            {
                ctx.Inject<AgentSessionScopeArguments>(out var arguments);
                ctx.Inject<IAgentSessionScope>(out var scope);
                ctx.Inject<ToolOutputBlobStore>(out var outputBlobs);
                return new AgentTaskService(
                    scope,
                    arguments.Identity.SessionId,
                    arguments.Router,
                    arguments.AgentTasks,
                    new AgentTaskNotifier(scope, outputBlobs, arguments.Diagnostics),
                    arguments.EventBroker,
                    arguments.EventRepository,
                    arguments.Diagnostics,
                    arguments.Lifetime);
            })
            .Bind<IReadOnlyList<string>>().To((AgentSessionScopeArguments arguments) => arguments.ReadOnlyExecCommandPrefixes)
            .Bind<IQuestionBroker>().To((AgentSessionScopeArguments arguments) => arguments.UserQuestions)
            .Bind<CompactionGroupBlobStore>().As(Lifetime.Scoped).To(ctx =>
            {
                ctx.Inject<AgentSessionScopeArguments>(out var arguments);
                return new CompactionGroupBlobStore(arguments.Scratch);
            })
            .Bind<Compactor>().To((AgentSessionScopeArguments arguments) => arguments.Compactor)
            .Bind<IPromptTemplateCatalog>().To((AgentSessionScopeArguments arguments) => arguments.PromptTemplates)
            .Bind<IAgentProfile>().To((AgentSessionScopeArguments arguments) => arguments.Profile)
            .Bind<IRuntimeStatus>().As(Lifetime.Scoped).To(ctx =>
            {
                ctx.Inject<AgentSessionScopeArguments>(out var arguments);
                ctx.Inject<IAgentTaskService>(out var agentTasks);
                return new RuntimeStatus(
                    arguments.PromptTemplates,
                    arguments.TimeProvider,
                    [
                        new RuntimeTreeStatusProvider(arguments.Registry, arguments.PromptTemplates),
                        new AgentTaskStatusProvider(agentTasks, arguments.PromptTemplates),
                    ]);
            })
            .Bind<TimeProvider>().To((AgentSessionScopeArguments arguments) => arguments.TimeProvider)
            .Bind<AgentSessionSecurity>().To((AgentSessionScopeArguments arguments) => arguments.Security)
            .Bind<AgentSkills>().To((AgentSessionScopeArguments arguments) => arguments.Skills)
            .Bind<ContextCadence>().As(Lifetime.Scoped).To<ContextCadence>()
            .Bind<IDiagnosticLog>().To((AgentSessionScopeArguments arguments) => arguments.Diagnostics)
            .Bind<ProviderSessions>().As(Lifetime.Scoped).To(ctx =>
            {
                ctx.Inject<AgentSessionScopeArguments>(out var arguments);
                return new ProviderSessions(
                    arguments.Diagnostics,
                    arguments.Identity.SessionId,
                    new LastRequestDumper(
                        Path.Combine(arguments.Scratch.Root, "last_request.json"),
                        arguments.Diagnostics));
            })
            .Bind<IPermissionBroker>().To((AgentSessionScopeArguments arguments) => arguments.Permissions)
            .Bind<IAgentQueues>().As(Lifetime.Scoped).To(ctx =>
            {
                ctx.Inject<AgentSessionScopeArguments>(out var arguments);
                ctx.Inject<IChildRegistry>(out var children);
                return new AgentQueues(arguments.Identity, arguments.ParentLink.Parent?.GetService<IAgentQueues>(), arguments.Resources, children, static queueIdentity => new QueueInventory(queueIdentity), arguments.Diagnostics);
            })
            .Bind<QueueSnapshotPublisher>().As(Lifetime.Scoped).To(ctx =>
            {
                ctx.Inject<AgentSessionScopeArguments>(out var arguments);
                ctx.Inject<IAgentQueues>(out var queues);
                var root = arguments.ParentLink.Parent;
                while (root?.ParentScope.Parent is { } parent)
                {
                    root = parent;
                }

                return new QueueSnapshotPublisher(queues, arguments.EventBroker, root?.Session.SessionId ?? arguments.Identity.SessionId);
            })
            .Bind<ProcessSnapshotPublisher>().As(Lifetime.Scoped).To<ProcessSnapshotPublisher>()
            .Bind<IReadOnlyList<IInventoryPublisher>>().As(Lifetime.Scoped).To(ctx =>
            {
                ctx.Inject<QueueSnapshotPublisher>(out var queues);
                ctx.Inject<ProcessSnapshotPublisher>(out var processes);
                return new IInventoryPublisher[] { queues, processes };
            })
            .Bind<IReadOnlyList<IAgentWorkOwner>>().As(Lifetime.Scoped).To(ctx =>
            {
                ctx.Inject<IAgentTaskService>(out var agentTasks);
                ctx.Inject<IProcessOwner>(out var processes);
                return new IAgentWorkOwner[] { agentTasks, processes };
            })
            .Bind<ExecCommandToolFactory>().As(Lifetime.Scoped).To<ExecCommandToolFactory>()
            .Bind<WriteStdinToolFactory>().As(Lifetime.Scoped).To<WriteStdinToolFactory>()
            .Bind<InterruptProcessToolFactory>().As(Lifetime.Scoped).To<InterruptProcessToolFactory>()
            .Bind<MonitorToolFactory>().As(Lifetime.Scoped).To<MonitorToolFactory>()
            .Bind<QuestionToolFactory>().As(Lifetime.Scoped).To<QuestionToolFactory>()
            .Bind<AnswerToolFactory>().As(Lifetime.Scoped).To<AnswerToolFactory>()
            .Bind<ReadToolFactory>().As(Lifetime.Scoped).To<ReadToolFactory>()
            .Bind<ReadImageToolFactory>().As(Lifetime.Scoped).To<ReadImageToolFactory>()
            .Bind<ImageGenerationToolFactory>().As(Lifetime.Scoped).To<ImageGenerationToolFactory>()
            .Bind<GlobToolFactory>().As(Lifetime.Scoped).To<GlobToolFactory>()
            .Bind<WriteToolFactory>().As(Lifetime.Scoped).To<WriteToolFactory>()
            .Bind<EditToolFactory>().As(Lifetime.Scoped).To<EditToolFactory>()
            .Bind<WebFetchToolFactory>().As(Lifetime.Scoped).To<WebFetchToolFactory>()
            .Bind<AgentSpawnToolFactory>().As(Lifetime.Scoped).To<AgentSpawnToolFactory>()
            .Bind<GetAgentTasksToolFactory>().As(Lifetime.Scoped).To(ctx =>
            {
                ctx.Inject<AgentSessionScopeArguments>(out var arguments);
                ctx.Inject<IAgentTaskService>(out var agentTasks);
                return new GetAgentTasksToolFactory(agentTasks, arguments.ToolDefinitions);
            })
            .Bind<SetAgentTasksToolFactory>().As(Lifetime.Scoped).To(ctx =>
            {
                ctx.Inject<AgentSessionScopeArguments>(out var arguments);
                ctx.Inject<IAgentTaskService>(out var agentTasks);
                return new SetAgentTasksToolFactory(arguments.Workspace, agentTasks, arguments.PromptTemplates, arguments.ToolDefinitions);
            })
            .Bind<ICheckpointService>().As(Lifetime.Scoped).To(ctx =>
            {
                ctx.Inject<AgentSessionScopeArguments>(out var arguments);
                return new CheckpointService(arguments.EventRepository, arguments.Identity.SessionId);
            })
            .Bind<SetCheckpointToolFactory>().As(Lifetime.Scoped).To<SetCheckpointToolFactory>()
            .Bind<SetExitReminderToolFactory>().As(Lifetime.Scoped).To(ctx =>
            {
                ctx.Inject<AgentSessionScopeArguments>(out var arguments);
                ctx.Inject<IExitReminder>(out var reminder);
                return new SetExitReminderToolFactory(reminder, arguments.PromptTemplates, arguments.ToolDefinitions);
            })
            .Bind<ClearExitReminderToolFactory>().As(Lifetime.Scoped).To(ctx =>
            {
                ctx.Inject<AgentSessionScopeArguments>(out var arguments);
                ctx.Inject<IExitReminder>(out var reminder);
                return new ClearExitReminderToolFactory(reminder, arguments.PromptTemplates, arguments.ToolDefinitions);
            })
            .Bind<AgentSendToolFactory>().As(Lifetime.Scoped).To<AgentSendToolFactory>()
            .Bind<AgentInterruptToolFactory>().As(Lifetime.Scoped).To(ctx =>
            {
                ctx.Inject<AgentSessionScopeArguments>(out var arguments);
                ctx.Inject<IChildRegistry>(out var children);
                return new AgentInterruptToolFactory(children, arguments.ToolDefinitions);
            })
            .Bind<AgentStatusToolFactory>().As(Lifetime.Scoped).To<AgentStatusToolFactory>()
            .Bind<WaitToolFactory>().As(Lifetime.Scoped).To<WaitToolFactory>()
            .Bind<StatusToolFactory>().As(Lifetime.Scoped).To<StatusToolFactory>()
            .Bind<CompactContextToolFactory>().As(Lifetime.Scoped).To<CompactContextToolFactory>()
            .Bind<QueueCreateToolFactory>().As(Lifetime.Scoped).To<QueueCreateToolFactory>()
            .Bind<QueueInfoToolFactory>().As(Lifetime.Scoped).To<QueueInfoToolFactory>()
            .Bind<QueuePushToolFactory>().As(Lifetime.Scoped).To<QueuePushToolFactory>()
            .Bind<QueueTakeToolFactory>().As(Lifetime.Scoped).To<QueueTakeToolFactory>()
            .Bind<RequestWritePermissionToolFactory>().As(Lifetime.Scoped).To<RequestWritePermissionToolFactory>()
            .Bind<IExitReminder>().As(Lifetime.Scoped).To(ctx =>
            {
                ctx.Inject<AgentSessionScopeArguments>(out var arguments);
                return new ExitReminder(
                    arguments.EventRepository,
                    arguments.EventBroker,
                    arguments.PromptTemplates,
                    arguments.Identity.SessionId);
            })
            .Bind<PendingChildQuestionTurnCompletionCallback>().As(Lifetime.Scoped).To<PendingChildQuestionTurnCompletionCallback>()
            .Bind<ActiveWorkTurnCompletionCallback>().As(Lifetime.Scoped).To<ActiveWorkTurnCompletionCallback>()
            .Bind<ExitReminderTurnCompletionCallback>().As(Lifetime.Scoped).To<ExitReminderTurnCompletionCallback>()
            .Bind<IReadOnlyList<IAgentTurnCompletionCallback>>("turnCompletionCallbacks").As(Lifetime.Scoped).To(ctx =>
            {
                ctx.Inject<PendingChildQuestionTurnCompletionCallback>(out var pendingChildQuestions);
                ctx.Inject<ActiveWorkTurnCompletionCallback>(out var activeWork);
                ctx.Inject<ExitReminderTurnCompletionCallback>(out var exitReminder);
                return new IAgentTurnCompletionCallback[]
                {
                    pendingChildQuestions,
                    activeWork,
                    exitReminder,
                };
            })
            .Bind<IReadOnlyList<IToolFactory>>("toolFactories").As(Lifetime.Scoped).To(ctx =>
            {
                ctx.Inject<ExecCommandToolFactory>(out var execCommand);
                ctx.Inject<WriteStdinToolFactory>(out var writeStdin);
                ctx.Inject<InterruptProcessToolFactory>(out var interruptProcess);
                ctx.Inject<MonitorToolFactory>(out var monitor);
                ctx.Inject<QuestionToolFactory>(out var question);
                ctx.Inject<AnswerToolFactory>(out var answer);
                ctx.Inject<ReadToolFactory>(out var read);
                ctx.Inject<ReadImageToolFactory>(out var readImage);
                ctx.Inject<ImageGenerationToolFactory>(out var imageGeneration);
                ctx.Inject<GlobToolFactory>(out var glob);
                ctx.Inject<WriteToolFactory>(out var write);
                ctx.Inject<EditToolFactory>(out var edit);
                ctx.Inject<WebFetchToolFactory>(out var webFetch);
                ctx.Inject<AgentSpawnToolFactory>(out var agentSpawn);
                ctx.Inject<SetAgentTasksToolFactory>(out var setAgentTasks);
                ctx.Inject<GetAgentTasksToolFactory>(out var getAgentTasks);
                ctx.Inject<SetCheckpointToolFactory>(out var setCheckpoint);
                ctx.Inject<SetExitReminderToolFactory>(out var setExitReminder);
                ctx.Inject<ClearExitReminderToolFactory>(out var clearExitReminder);
                ctx.Inject<AgentSendToolFactory>(out var agentSend);
                ctx.Inject<AgentInterruptToolFactory>(out var agentInterrupt);
                ctx.Inject<AgentStatusToolFactory>(out var agentStatus);
                ctx.Inject<WaitToolFactory>(out var wait);
                ctx.Inject<StatusToolFactory>(out var status);
                ctx.Inject<CompactContextToolFactory>(out var compactContext);
                ctx.Inject<QueueCreateToolFactory>(out var queueCreate);
                ctx.Inject<QueueInfoToolFactory>(out var queueInfo);
                ctx.Inject<QueuePushToolFactory>(out var queuePush);
                ctx.Inject<QueueTakeToolFactory>(out var queueTake);
                ctx.Inject<RequestWritePermissionToolFactory>(out var requestWritePermission);
                return new IToolFactory[]
                {
                    execCommand,
                    writeStdin,
                    interruptProcess,
                    monitor,
                    question,
                    answer,
                    read,
                    readImage,
                    imageGeneration,
                    glob,
                    write,
                    edit,
                    webFetch,
                    agentSpawn,
                    setAgentTasks,
                    getAgentTasks,
                    setCheckpoint,
                    setExitReminder,
                    clearExitReminder,
                    agentSend,
                    agentInterrupt,
                    agentStatus,
                    wait,
                    status,
                    compactContext,
                    queueCreate,
                    queueInfo,
                    queuePush,
                    queueTake,
                    requestWritePermission,
                };
            })
            .Bind<IReadOnlyList<IActiveWorkBlocker>>().As(Lifetime.Scoped).To(ctx =>
            {
                ctx.Inject<AgentSessionScopeArguments>(out var arguments);
                ctx.Inject<IProcessOwner>(out var processes);
                ctx.Inject<IAgentTaskService>(out var agentTasks);
                ctx.Inject<IChildRegistry>(out var children);
                ctx.Inject<IAgentQueues>(out var queues);
                return new IActiveWorkBlocker[]
                {
                    new ChildAgentActiveWorkBlocker(children, arguments.Identity),
                    new ProcessActiveWorkBlocker(processes),
                    new AgentTaskActiveWorkBlocker(agentTasks, arguments.PromptTemplates),
                    new QueueActiveWorkBlocker(queues, arguments.PromptTemplates),
                };
            })
            .Bind().As(Lifetime.Scoped).To(ctx =>
            {
                ctx.Inject<AgentSessionScopeArguments>(out var arguments);
                ctx.Inject<IReadOnlyList<IActiveWorkBlocker>>(out var blockers);
                return new ActiveWorkCompletionReminder(blockers, arguments.PromptTemplates);
            })
            .Bind().As(Lifetime.Scoped).To(ctx =>
            {
                ctx.Inject<AgentSessionScopeArguments>(out var arguments);
                return new ToolOutputBlobStore(arguments.Scratch.BlobDirectory);
            })
            .Bind().As(Lifetime.Scoped).To(ctx =>
            {
                ctx.Inject<AgentSessionScopeArguments>(out var arguments);
                return new AgentOutputFile(arguments.Scratch.BlobDirectory);
            })
            .Bind().As(Lifetime.Scoped).To(ctx =>
            {
                ctx.Inject<AgentSessionScopeArguments>(out var arguments);
                return new AgentSessionActivity(arguments.TimeProvider);
            })
            .Bind<IAgentResolver>().As(Lifetime.Scoped).To(ctx =>
            {
                ctx.Inject<AgentSessionScopeArguments>(out var arguments);
                ctx.Inject<IAgentParentScope>(out var parentScope);
                ctx.Inject<IAgentSessionScope>(out var scope);
                return new AgentResolver(arguments.Identity, parentScope, scope, arguments.Registry);
            })
            .Bind<IAgentSession>().As(Lifetime.Scoped).To<AgentSession>()
            .Bind<IGoalService>().As(Lifetime.Scoped).To<GoalService>()
            .Root<IAgentSession>("Session")
            .Root<IGoalService>("Goals")
            .Root<IAgentSpawner>("AgentSpawner")
            .Root<IAgentTaskService>("AgentTasks")
            .Root<IChildRegistry>("ChildRegistry")
            .Root<IAgentParentScope>("ParentScope")
            .Root<IChildQuestion>("ChildQuestion")
            .Root<IChildQuestionCoordinator>("ChildQuestions")
            .Root<IProcessOwner>("Processes")
            .Root<IAgentQueues>("Queues")
            .Root<IRuntimeStatus>("Status")
            .Root<IReadOnlyList<IInventoryPublisher>>("Publishers")
            .Root<IReadOnlyList<IAgentWorkOwner>>("WorkOwners");
}
