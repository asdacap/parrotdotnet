using Parrot.Agent;

namespace Parrot.Tools;

// A tool the model may call. Display or behavioural differences live on the
// tool -- never a branch on its name elsewhere (AGENTS.md). Model-facing
// definitions come from configuration and are paired by ToolSnapshot.
//
// An instance belongs to one AgentSession and is built by its IToolFactory.
// Turn-varying policy arrives as the immutable selection captured for the call.
internal interface ITool
{
    string Name { get; }

    Task<ToolExecutionResult> Execute(
        ToolInvocation invocation,
        AgentTurnSelection selection,
        CancellationToken cancellationToken);
}
