using Parrot.Agent;

namespace Parrot.Tools;

// A tool the model may call. Display or behavioural differences live on the
// tool -- never a branch on its name elsewhere (AGENTS.md). Model-facing
// definitions come from configuration and are paired by ToolSnapshot; a tool
// may adjust its own description through Describe.
//
// An instance belongs to one AgentSession and is built by its IToolFactory.
// Turn-varying policy arrives as the immutable selection captured for the call.
internal interface ITool
{
    string Name { get; }

    // True keeps the tool offered on the final provider request of a turn, reached
    // via the turn limit or via agent_interrupt; only tools that settle active work
    // or answer children qualify.
    bool IsEnabledAfterInterruption => false;

    // Returns the model-facing description for this session, given the configured
    // tools.<name>.description; tools that vary their wording override this.
    string Describe(string configuredDescription) => configuredDescription;

    // True permits this invocation to run concurrently with other parallel-safe calls;
    // false keeps it out of parallel batches in the agent session.
    bool IsParallelSafe(ToolInvocation invocation) => false;

    Task<ToolExecutionResult> Execute(
        ToolInvocation invocation,
        AgentTurnSelection selection,
        CancellationToken cancellationToken);
}
