using Parrot.Agent;
using Parrot.Process;
using Parrot.Protocol;
using Parrot.Questions;
using Parrot.Queues;

namespace Parrot.Core.Tests;

internal sealed class ResourceResolverFixture(IAgentQueues? queues, IProcessOwner? processes) : IAgentResolver
{
    private readonly IAgentSessionScope? _scope = queues is null ? null : new ResourceScope(queues, processes);

    public IAgentSessionScope ResolveStatusTargetScope(string name) => throw new NotSupportedException();

    public IAgentSession ResolveStatusTarget(string name) => throw new NotSupportedException();

    public IAgentSession ResolveRecipient(string nameOrPath) => throw new NotSupportedException();

    public bool IsAncestor(IAgentSession session) => throw new NotSupportedException();

    public (IAgentSessionScope Scope, string Name) ResolveResource(string path)
    {
        if (_scope is null)
        {
            throw new AgentRegistryException("resource owner unavailable");
        }

        var slash = path.LastIndexOf('/');
        if (slash < 0 || slash == path.Length - 1)
        {
            throw new AgentRegistryException("invalid resource path");
        }

        return (_scope, path[(slash + 1)..]);
    }

    private sealed class ResourceScope : IAgentSessionScope
    {
        private readonly AgentSessionServices _services = new();

        public ResourceScope(IAgentQueues queues, IProcessOwner? processes)
        {
            _services.Register(queues);
            if (processes is not null)
            {
                _services.Register(processes);
            }
        }

        public IAgentSession Session => throw new NotSupportedException();

        public IGoalService Goals => throw new NotSupportedException();

        public IAgentSpawner AgentSpawner => throw new NotSupportedException();

        public IChildRegistry ChildRegistry => throw new NotSupportedException();

        public IAgentParentScope ParentScope => throw new NotSupportedException();

        public IChildQuestionCoordinator ChildQuestions => throw new NotSupportedException();

        public T GetService<T>()
            where T : class => _services.GetService<T>();

        public void PublishSnapshots() => throw new NotSupportedException();

        public IReadOnlyList<Event> CaptureSnapshotEvents() => throw new NotSupportedException();

        public Task SettleWork() => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
