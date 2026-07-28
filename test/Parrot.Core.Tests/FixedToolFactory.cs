using Parrot.Agent;
using Parrot.Tools;

namespace Parrot.Core.Tests;

// Hands out the tool it was given. A test tool has nothing to build per
// session, so the factory that production needs is a pass-through here.
internal sealed record FixedToolFactory(ITool Tool) : IToolFactory
{
    public ITool Create(AgentSession session, AgentSelection selection) => Tool;
}
