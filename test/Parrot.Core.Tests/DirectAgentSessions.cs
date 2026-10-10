using Parrot.Agent;
using Parrot.Config;
using Parrot.Context;
using Parrot.Events;
using Parrot.Llm;
using Parrot.Process;
using Parrot.Queues;
using Parrot.Security;
using Parrot.Store;
using Parrot.Tools;

namespace Parrot.Core.Tests;

// Builds real agent sessions with no tools. The production factory is composed
// around a ProcessRunner and a sandbox, which a test of the wire contract has
// no use for -- but the session itself has to be real, because the drain is
// what the contract is a contract over.
internal sealed class DirectAgentSessions : IAgentSessionFactorySource
{
    private readonly List<AgentIdentity> _identities = [];
    private readonly List<AgentSessionSecurity> _securities = [];
    private readonly List<IAgentQueues> _queues = [];
    private readonly List<IAgentSession> _sessions = [];
    private readonly List<IUserSession> _owners = [];
    private IModelRouter? _router;
    private TimeProvider _timeProvider = TimeProvider.System;
    private bool _includeStatusTool;

    public IReadOnlyList<AgentIdentity> Identities => _identities;

    public IReadOnlyList<AgentSessionSecurity> Securities => _securities;

    public IReadOnlyList<IAgentQueues> Queues => _queues;

    public IReadOnlyList<IAgentSession> Sessions => _sessions;

    public IReadOnlyList<IUserSession> Owners => _owners;

    public void IncludeStatusTool() => _includeStatusTool = true;

    public void Use(IModelRouter router) => _router = router;

    public void UseTimeProvider(TimeProvider timeProvider) => _timeProvider = timeProvider;

    public IAgentSessionFactory Create(IUserSession owner)
    {
        _owners.Add(owner);
        return new OwnerAgentSessions(this, owner);
    }

    private sealed class OwnerAgentSessions(DirectAgentSessions source, IUserSession owner) : IAgentSessionFactory
    {
        public IAgentHistory PrepareHistory(AgentIdentity identity, IEventRepository repository) =>
            repository.BindAgentHistory(identity.SessionId, new AgentHistoryFile(owner.Resources.AgentScratch(identity.NamePath), [identity.SessionId]));

        public IAgentSessionScope Create(
            AgentIdentity identity,
            AgentSessionParentLink parentLink,
            ModelSelector model,
            IEventBroker eventBroker,
            IAgentHistory history,
            IUserSessionStatistics statistics,
            IAgentProfile profile,
            SecurityProfile securityProfile,
            IAgentRegistry registry,
            CancellationToken lifetime)
        {
            history.Refresh();
            source._identities.Add(identity);
            var router = source._router ?? throw new InvalidOperationException("model router is not configured");
            var security = new SecurityProfileTestFixture(securityProfile).Security;
            source._securities.Add(security);
            var scope = TestAgentSessionScope.BuildWithResources(
                identity,
                parentLink,
                registry,
                TestModels.PromptTemplates,
                owner.Resources,
                TestModels.Runner(string.Empty),
                owner.Diagnostics,
                (sessionParentScope, owningScope, children, childQuestions) =>
            {
                var processes = owningScope.GetService<IProcessOwner>();
                var queues = owningScope.GetService<IAgentQueues>();
                var status = TestModels.ScopedRuntimeStatus(registry, owningScope);
                source._queues.Add(queues);
                var exitReminder = new ExitReminder(history, eventBroker, TestModels.PromptTemplates, identity.SessionId);
                IAgentSession session = TestModels.ComposeSession(identity, sessionParentScope, model, router, eventBroker, history, statistics, statistics.GetAgentStatistics(identity.SessionId), source._includeStatusTool ? [new StatusToolFactory(status, TestModels.ToolDefinitions)] : [], TestModels.MaterializePrompt(identity, ".", "."), new ToolOutputBlobStore(Path.GetTempPath()), new AgentOutputFile(Path.GetTempPath()), TestModels.CompactionGroupBlobs(), new Compactor(90, 30, 60_000, 1024, TestModels.PromptTemplates), new ProviderSessions(owner.Diagnostics, identity.SessionId, null), new ContextCadence(), TestModels.PromptTemplates, exitReminder, profile, new TestCompletionCallbacksFixture(childQuestions, new ActiveWorkCompletionReminder([new ChildAgentActiveWorkBlocker(children, identity), new ProcessActiveWorkBlocker(processes), new QueueActiveWorkBlocker(queues, TestModels.PromptTemplates)], TestModels.PromptTemplates), exitReminder, history, eventBroker).Callbacks, security, TestModels.EmptySkills(TestModels.PromptTemplates), new RequestLimitsConfig(), status, new AgentSessionActivity(source._timeProvider), owner.Diagnostics, lifetime);
                source._sessions.Add(session);
                return session;
            },
                lifetime);
            TestModels.RegisterScope(scope);
            return scope;
        }
    }
}
