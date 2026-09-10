using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Parrot.Agent;
using Parrot.Config;
using Parrot.Context;
using Parrot.Events;
using Parrot.Llm;
using Parrot.Process;
using Parrot.Questions;
using Parrot.Queues;
using Parrot.Security;
using Parrot.State;
using Parrot.Statuses;
using Parrot.Store;
using Parrot.Tools;

namespace Parrot.Core.Tests;

internal static class TestModels
{
    private static readonly ConcurrentBag<AgentQueueCatalog> QueueCatalogs = [];
    private static readonly ConcurrentBag<Parrot.Process.ShellProcessOwners> ProcessOwners = [];
    private static readonly ConcurrentBag<IAgentRegistry> Registries = [];
    private static readonly ConditionalWeakTable<IAgentSession, IAgentSessionScope> Scopes = [];

    public static IReadOnlyDictionary<string, ProfileConfig> Profiles { get; } =
        new Dictionary<string, ProfileConfig>(StringComparer.Ordinal)
        {
            [ModeRegistry.Build] = new ProfileConfig(
                "You are Parrot's build mode. Implement and verify the requested changes.",
                "Test profile.",
                null,
                64,
                3,
                false,
                true,
                true,
                false,
                []),
            [ModeRegistry.Plan] = new ProfileConfig(
                "You are Parrot's plan mode. Inspect the project and write the complete implementation plan as Markdown",
                "Test profile.",
                null,
                64,
                3,
                true,
                true,
                true,
                false,
                []),
            [ModeRegistry.Query] = new ProfileConfig(
                "You are Parrot's query mode. Inspect the project and answer the user's question without making changes.",
                "Test profile.",
                null,
                64,
                3,
                true,
                true,
                true,
                false,
                []),
            ["explorer"] = new ProfileConfig(
                "You are an explorer agent.",
                "Test read-only child profile.",
                null,
                32,
                3,
                true,
                true,
                false,
                true,
                []),
            ["worker"] = new ProfileConfig(
                "You are a worker agent.",
                "Test child profile.",
                null,
                64,
                3,
                false,
                true,
                false,
                true,
                []),
            ["agent-task-prepare"] = new ProfileConfig(
                "You are an AgentTask preparation agent.",
                "Prepare an AgentTask and return its preparation context.",
                null,
                128,
                4,
                false,
                true,
                false,
                true,
                []),
            ["agent-task-payload"] = new ProfileConfig(
                "You are an AgentTask payload executor. Implement and verify the assigned task. For an instruction leaf, use this one retained session to emit the strict combined JSON result with nonblank context and exactly one verdict.",
                "Implement and verify an AgentTask payload and report its observable result. For an instruction leaf, return only strict JSON: accept with nonblank evidence, reject_and_halt with nonblank feedback, or reject_and_retry with nonblank feedback and a replacement instruction or task array.",
                null,
                128,
                4,
                false,
                true,
                false,
                true,
                []),
            ["agent-task-validation"] = new ProfileConfig(
                "You are an AgentTask acceptance validator.",
                "Validate an AgentTask result against its acceptance criteria.",
                null,
                128,
                4,
                false,
                true,
                false,
                true,
                []),
            ["test"] = new ProfileConfig(
                string.Empty,
                "Default test profile.",
                null,
                24,
                3,
                false,
                false,
                false,
                true,
                []),
        };

    public static PromptTemplateCatalog PromptTemplates { get; } = LoadPromptTemplates();

    public static ToolDefinitionCatalog EmptyToolDefinitions { get; } = new(
        new Dictionary<string, ConfiguredToolDefinition>(StringComparer.Ordinal));

    public static CompactionGroupBlobStore CompactionGroupBlobs() =>
        new(new AgentScratchDirectory(Path.Combine(
            Path.GetTempPath(),
            "parrot-tests",
            Guid.NewGuid().ToString("N"),
            "scratch")));

    public static AgentQueues Queues(AgentIdentity identity)
    {
        var root = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "parrot-tests", Guid.NewGuid().ToString("N"))).FullName;
        var resources = new UserSessionResources(
            new StatePaths(root, root, root),
            UserSessionId.Parse(Guid.NewGuid().ToString("N")),
            ProjectWorkspace.FromLaunchDirectory(root));
        var catalog = new AgentQueueCatalog(resources, TestDiagnosticLog.Instance);
        QueueCatalogs.Add(catalog);
        if (identity.ParentSessionId.Length > 0)
        {
            _ = catalog.Register(AgentIdentity.Main(identity.ParentSessionId, identity.ParentSessionName, TestModels.PromptTemplates));
        }

        return catalog.Register(identity);
    }

    public static IAgentSessionScope ScopeOf(IAgentSession session) =>
        Scopes.TryGetValue(session, out var scope)
            ? scope
            : throw new InvalidOperationException($"scope is not registered: {session.SessionId}");

    public static void RegisterScope(IAgentSessionScope scope) => Scopes.Add(scope.Session, scope);

    public static void UnregisterScope(IAgentSessionScope scope) => _ = Scopes.Remove(scope.Session);

    public static ChildQuestionCoordinator CreateChildQuestions(IAgentSessionScope owner) =>
        owner.ChildQuestions;

    public static IAgentRegistry Registry(
        IAgentSessionFactory agentSessions,
        EventBroker eventBroker,
        EventRepository eventRepository,
        ProfileRegistry profiles,
        PromptTemplateCatalog promptTemplates,
        CancellationToken lifetime) =>
        RegistryWithBudget(
            agentSessions,
            eventBroker,
            eventRepository,
            profiles,
            promptTemplates,
            new RetainedAgentBudget(1024),
            lifetime);

    public static IAgentRegistry RegistryWithBudget(
        IAgentSessionFactory agentSessions,
        EventBroker eventBroker,
        EventRepository eventRepository,
        ProfileRegistry profiles,
        PromptTemplateCatalog promptTemplates,
        RetainedAgentBudget retainedAgents,
        CancellationToken lifetime)
    {
        var root = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "parrot-tests", Guid.NewGuid().ToString("N"))).FullName;
        var resources = new UserSessionResources(
            new StatePaths(root, root, root),
            UserSessionId.Parse(Guid.NewGuid().ToString("N")),
            ProjectWorkspace.FromLaunchDirectory(root));
        var processes = new ShellProcessOwners(resources, new ProcessRunner(string.Empty), TestDiagnosticLog.Instance, lifetime);
        var catalog = new AgentQueueCatalog(resources, TestDiagnosticLog.Instance);
        IAgentRegistry registry = new AgentRegistry(agentSessions, eventBroker, eventRepository, profiles, promptTemplates, retainedAgents, TestDiagnosticLog.Instance, lifetime);
        registry.AttachStatus(new RuntimeStatus(catalog, new ShellProcessOwnersStatusSource(processes), new AgentRegistryStatusSource(registry), TestModels.PromptTemplates, TimeProvider.System));
        ProcessOwners.Add(processes);
        QueueCatalogs.Add(catalog);
        Registries.Add(registry);
        return registry;
    }

    public static AgentSessionDependencies Dependencies(
        AgentIdentity identity,
        EventBroker eventBroker,
        EventRepository eventRepository,
        CancellationToken lifetime)
    {
        var root = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "parrot-tests", Guid.NewGuid().ToString("N"))).FullName;
        var resources = new UserSessionResources(
            new StatePaths(root, root, root),
            UserSessionId.Parse(Guid.NewGuid().ToString("N")),
            ProjectWorkspace.FromLaunchDirectory(root));
        var processes = new ShellProcessOwners(resources, new ProcessRunner(string.Empty), TestDiagnosticLog.Instance, lifetime);
        var catalog = new AgentQueueCatalog(resources, TestDiagnosticLog.Instance);
        IAgentRegistry registry = new AgentRegistry(
            new UnsupportedAgentSessionFactory(),
            eventBroker,
            eventRepository,
            new TestProfileFixture().Registry,
            TestModels.PromptTemplates,
            new RetainedAgentBudget(1024),
            TestDiagnosticLog.Instance,
            lifetime);
        var status = new RuntimeStatus(catalog, new ShellProcessOwnersStatusSource(processes), new AgentRegistryStatusSource(registry), TestModels.PromptTemplates, TimeProvider.System);
        registry.AttachStatus(status);
        if (identity.ParentSessionId.Length > 0)
        {
            _ = catalog.Register(AgentIdentity.Main(identity.ParentSessionId, identity.ParentSessionName, TestModels.PromptTemplates));
        }

        var queues = catalog.Register(identity);
        var owner = processes.Prepare(identity.SessionId, new AgentPathEnvironment(resources, resources.AgentScratch(identity.SessionId)));
        processes.Register(owner);
        ProcessOwners.Add(processes);
        QueueCatalogs.Add(catalog);
        Registries.Add(registry);
        return new AgentSessionDependencies(
            identity,
            owner,
            eventRepository,
            status,
            registry,
            queues);
    }

    public static ISystemPrompt MaterializePrompt(
        AgentIdentity identity,
        string workingDirectory,
        string configDirectory) =>
        new TestSystemPromptFixture(workingDirectory, configDirectory).Provider.Materialize(identity);

    public static ModelRouter Route(ProviderModel model)
    {
        var catalogModel = model.Variant is null
            ? model.Model
            : model.Model with
            {
                Capabilities = model.Model.Capabilities with
                {
                    Variants = [.. model.Model.Capabilities.Variants, model.Variant],
                },
            };
        var registry = new ProviderRegistry(
            [model.Provider],
            new Dictionary<string, IReadOnlyList<LLMModel>>(StringComparer.Ordinal)
            {
                [model.Provider.Id] = [catalogModel],
            });
        return new ModelRouter(registry, new ModelRouting(new ModelAliasCatalog(registry, []), model.Selector));
    }

    public static ResolvedModelSelection Resolve(ProviderModel model) => Route(model).Resolve(model.Selector);

    private static PromptTemplateCatalog LoadPromptTemplates()
    {
        var root = Path.Combine(Path.GetTempPath(), "parrot-tests", Guid.NewGuid().ToString("N"));
        return Configuration.Load(
            Path.Combine(root, "config.yaml"),
            Path.Combine(root, "predefined_config.yaml"))
            .PromptTemplates;
    }

    private sealed class UnsupportedAgentSessionFactory : IAgentSessionFactory
    {
        public IAgentSessionScope Create(
            AgentIdentity identity,
            AgentSessionParentLink parentLink,
            ModelSelector model,
            EventBroker eventBroker,
            EventRepository eventRepository,
            IMode mode,
            SecurityProfile securityProfile,
            RuntimeStatus status,
            IAgentRegistry registry,
            CancellationToken lifetime) =>
            throw new NotSupportedException("This test session does not support spawning subagents.");
    }
}
