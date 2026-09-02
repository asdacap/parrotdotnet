using Parrot.Agent;
using Parrot.Context;
using Parrot.Process;
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
            .Bind<IReadOnlyList<IToolFactory>>("toolFactories").As(Lifetime.Scoped).To(ctx =>
            {
                ctx.Inject<AgentSessionScopeArguments>(out var arguments);
                ctx.Inject<ShellProcessOwner>(out var processes);
                return arguments.ToolFactories
                    .Prepend<IToolFactory>(new InterruptProcessToolFactory(processes))
                    .Prepend(new WriteStdinToolFactory(processes))
                    .Prepend(new ExecCommandToolFactory(processes, arguments.ReadOnlyExecCommandPrefixes))
                    .ToArray();
            })
            .Bind().As(Lifetime.Scoped).To(ctx =>
            {
                ctx.Inject<AgentSessionScopeArguments>(out var arguments);
                ctx.Inject<ShellProcessOwner>(out var processes);
                return new ActiveWorkCompletionReminder(
                    arguments.Identity.SessionId,
                    arguments.Registry,
                    processes,
                    arguments.PromptTemplates);
            })
            .Bind().As(Lifetime.Scoped).To(ctx =>
            {
                ctx.Inject<AgentSessionScopeArguments>(out var arguments);
                return new TodoCollection(
                    arguments.Identity.SessionId,
                    arguments.EventRepository,
                    arguments.EventBroker);
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
                ctx.Inject<TodoCollection>(out var todos);
                ctx.Inject<ToolOutputBlobStore>(out var toolOutputBlobs);
                ctx.Inject<ShellProcessOwner>(out var processes);
                ctx.Inject<ActiveWorkCompletionReminder>(out var activeWorkReminder);
                ctx.Inject<AgentSessionActivity>(out var activity);
                ctx.Inject<IReadOnlyList<IToolFactory>>("toolFactories", out var toolFactories);
                ctx.Inject<ISystemPrompt>(out var systemPrompt);
                var session = new AgentSession(
                    arguments.Identity,
                    arguments.Model,
                    arguments.Router,
                    arguments.EventBroker,
                    arguments.EventRepository,
                    toolFactories,
                    arguments.ToolDefinitions,
                    systemPrompt,
                    todos,
                    toolOutputBlobs,
                    arguments.Compactor,
                    arguments.PromptTemplates,
                    activeWorkReminder,
                    arguments.Mode,
                    arguments.Security,
                    arguments.Status,
                    arguments.Registry,
                    arguments.Queues,
                    activity,
                    arguments.Lifetime);
                arguments.Queues.Attach(session);
                arguments.ShellProcesses.Register(processes);
                return session;
            })
            .Root<AgentSession>("Session");
}
