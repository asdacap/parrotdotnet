using Parrot.Agent;

namespace Parrot.Tools;

internal sealed class GitDiffToolFactory(string workingDirectory) : IToolFactory
{
    public ITool Create(AgentSession session, AgentSelection selection) => new GitDiffTool(workingDirectory);
}
