using System.Collections.Concurrent;
using Parrot.Agent;
using Parrot.Config;
using Parrot.Context;
using Parrot.Events;
using Parrot.Llm;
using Parrot.Process;
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
    private static readonly ConcurrentBag<AgentRegistry> Registries = [];

    public static IReadOnlyDictionary<string, ProfileConfig> Profiles { get; } =
        new Dictionary<string, ProfileConfig>(StringComparer.Ordinal)
        {
            [ModeRegistry.Build] = Profile(
                "You are Parrot's build mode. Implement and verify the requested changes.",
                readOnly: false),
            [ModeRegistry.Plan] = Profile(
                "You are Parrot's plan mode. Inspect the project and write the complete implementation plan as Markdown",
                readOnly: true),
            [ModeRegistry.Query] = Profile(
                "You are Parrot's query mode. Inspect the project and answer the user's question without making changes.",
                readOnly: true),
            ["explorer"] = new ProfileConfig(
                "You are an explorer agent.",
                "Test read-only child profile.",
                null,
                32,
                3,
                true,
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
                []),
            ["test"] = new ProfileConfig(
                string.Empty,
                "Default test profile.",
                null,
                24,
                3,
                false,
                false,
                []),
        };

    public static ToolDocumentationCatalog EmptyToolDocumentation { get; } = new(
        new Dictionary<string, ToolDocumentation>(StringComparer.Ordinal));

    public static ToolDocumentationCatalog DocumentTools(
        params (string Name, IReadOnlyDictionary<string, ToolParameterDocumentation> Parameters)[] tools) => new(
        tools.ToDictionary(
            tool => tool.Name,
            tool => new ToolDocumentation("Test tool.", tool.Parameters),
            StringComparer.Ordinal));

    public static IReadOnlyDictionary<string, ToolParameterDocumentation> NoToolParameters() =>
        new Dictionary<string, ToolParameterDocumentation>(StringComparer.Ordinal);

    public static AgentQueues Queues(AgentIdentity identity)
    {
        var root = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "parrot-tests", Guid.NewGuid().ToString("N"))).FullName;
        var resources = new UserSessionResources(
            new StatePaths(root, root, root),
            UserSessionId.Parse(Guid.NewGuid().ToString("N")),
            ProjectWorkspace.FromLaunchDirectory(root));
        var catalog = new AgentQueueCatalog(resources);
        QueueCatalogs.Add(catalog);
        if (identity.ParentSessionId.Length > 0)
        {
            _ = catalog.Register(AgentIdentity.Main(identity.ParentSessionId, identity.ParentSessionName));
        }

        return catalog.Register(identity);
    }

    public static ProfileRegistry ProfileRegistry() =>
        new(Profiles, [], [], new HashSet<string>(StringComparer.Ordinal));

    public static IMode Profile()
    {
        var profile = ProfileRegistry().ResolveChild("test");
        return new NoopMode(profile, profile.SecurityProfile);
    }

    public static AgentRegistry Registry(
        IAgentSessionFactory agentSessions,
        EventBroker eventBroker,
        EventRepository eventRepository,
        ProfileRegistry profiles,
        CancellationToken lifetime)
    {
        var root = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "parrot-tests", Guid.NewGuid().ToString("N"))).FullName;
        var resources = new UserSessionResources(
            new StatePaths(root, root, root),
            UserSessionId.Parse(Guid.NewGuid().ToString("N")),
            ProjectWorkspace.FromLaunchDirectory(root));
        var processes = new ShellProcessOwners(resources, new ProcessRunner(string.Empty), lifetime);
        var catalog = new AgentQueueCatalog(resources);
        var registry = new AgentRegistry(agentSessions, eventBroker, eventRepository, profiles, lifetime);
        registry.AttachStatus(new RuntimeStatus(catalog, processes, registry));
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
        var processes = new ShellProcessOwners(resources, new ProcessRunner(string.Empty), lifetime);
        var catalog = new AgentQueueCatalog(resources);
        var registry = new AgentRegistry(
            new UnsupportedAgentSessionFactory(),
            eventBroker,
            eventRepository,
            ProfileRegistry(),
            lifetime);
        var status = new RuntimeStatus(catalog, processes, registry);
        registry.AttachStatus(status);
        if (identity.ParentSessionId.Length > 0)
        {
            _ = catalog.Register(AgentIdentity.Main(identity.ParentSessionId, identity.ParentSessionName));
        }

        var queues = catalog.Register(identity);
        var owner = processes.Prepare(identity.SessionId);
        processes.Register(owner);
        ProcessOwners.Add(processes);
        QueueCatalogs.Add(catalog);
        Registries.Add(registry);
        return new AgentSessionDependencies(
            new ActiveWorkCompletionReminder(identity.SessionId, registry, owner),
            Profile(),
            status,
            registry,
            queues);
    }

    public static ISystemPrompt MaterializePrompt(
        AgentIdentity identity,
        string workingDirectory,
        string configDirectory) =>
        PromptProvider(workingDirectory, configDirectory).Materialize(identity);

    public static ISystemPromptProvider PromptProvider(string workingDirectory, string configDirectory) =>
        new CompositeSystemPromptProvider(
            "test:system-prompt",
            [
                new ConfiguredSystemPromptProvider("runtime:system-context:01-base", "Test base prompt."),
                new AgentsPromptProvider(workingDirectory, configDirectory),
                new ExpectedCliUtilitiesProvider(EmptyCliUtilities()),
                new DateProvider("2026-07-24"),
                new PlatformProvider(),
                new WorkingDirectoryProvider(workingDirectory),
                new OptionalCliUtilitiesProvider(EmptyCliUtilities()),
                new SessionIdentityProvider(),
                new SubagentsProvider(ProfileRegistry()),
                new ModelPromptProvider(new Dictionary<string, string>(StringComparer.Ordinal)),
            ]);

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
        return new ModelRouter(registry, new ModelAliasCatalog(registry, []), model.Selector);
    }

    public static ResolvedModelSelection Resolve(ProviderModel model) => Route(model).Resolve(model.Selector);

    private static Parrot.Process.CliUtilityAvailability EmptyCliUtilities() =>
        Parrot.Process.CliUtilityAvailability.Inspect(
            new CliUtilityCandidates([], []),
            new Parrot.Process.ExecutableLocator(string.Empty, string.Empty));

    private static ProfileConfig Profile(string prompt, bool readOnly) => new(
        prompt,
        "Test profile.",
        null,
        64,
        3,
        readOnly,
        true,
        []);

    private sealed class UnsupportedAgentSessionFactory : IAgentSessionFactory
    {
        public IAgentSessionLease Create(
            AgentIdentity identity,
            ModelSelector model,
            EventBroker eventBroker,
            EventRepository eventRepository,
            IMode mode,
            SecurityProfile securityProfile,
            RuntimeStatus status,
            AgentRegistry registry,
            CancellationToken lifetime) =>
            throw new NotSupportedException("This test session does not support spawning subagents.");
    }
}
