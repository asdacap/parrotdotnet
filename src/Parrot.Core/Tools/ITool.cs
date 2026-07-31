using Parrot.Agent;

namespace Parrot.Tools;

// A tool the model may call. Display or behavioural differences live on the
// tool -- never a branch on its name elsewhere (AGENTS.md). ParametersJson is
// generated from the tool's input model; the snapshot forwards it, the provider
// sends it.
//
// An instance belongs to one AgentSession and is built by its IToolFactory.
// Turn-varying policy arrives as the immutable selection captured for the call.
internal interface ITool
{
    string Name { get; }

    string Description { get; }

    string ParametersJson { get; }

    Task<ToolExecutionResult> Execute(
        ToolInvocation invocation,
        AgentTurnSelection selection,
        CancellationToken cancellationToken);
}
