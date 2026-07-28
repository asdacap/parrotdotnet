using Parrot.Agent;
using Pure.DI;

namespace Parrot.Cli;

internal partial class AgentSessionComposition
{
    internal static void Setup() =>
        DI.Setup(nameof(AgentSessionComposition))
            .Hint(Hint.Resolve, "Off")
            .Arg<AgentSessionScopeArguments>("arguments")
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
                ctx.Inject<TodoCollection>(out var todos);
                return new AgentSession(
                    arguments.Identity,
                    arguments.Model,
                    arguments.Router,
                    arguments.EventBroker,
                    arguments.EventRepository,
                    arguments.ToolFactories,
                    arguments.SystemPromptProvider,
                    todos,
                    arguments.Compactor,
                    arguments.Profile,
                    arguments.SecurityProfile,
                    arguments.Status,
                    arguments.Lifetime);
            })
            .Root<AgentSession>("Session");
}
