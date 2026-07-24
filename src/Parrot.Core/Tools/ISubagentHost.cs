namespace Parrot.Tools;

// The capability agent_spawn needs: run a child agent to completion and return
// its result. An interface, not a delegate (PARROT0002), and the seam that lets
// a tool start a subagent without the tool knowing how sessions are built.
internal interface ISubagentHost
{
    Task<string> Spawn(string prompt, int depth, CancellationToken cancellationToken);
}
