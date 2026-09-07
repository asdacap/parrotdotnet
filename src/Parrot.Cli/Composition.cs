using Parrot.Agent;
using Parrot.Config;
using Parrot.Context;
using Parrot.Llm;
using Parrot.Process;
using Parrot.Protocol;
using Parrot.Skills;
using Parrot.State;
using Parrot.Store;
using Parrot.Web;
using Pure.DI;

namespace Parrot.Cli;

// The composition root, generated at compile time by Pure.DI: nested
// constructor calls, no container, no reflection, validated at build.
//
// AGENTS.md says "not IoC container"; this is a bounded exception, and the
// bounds are here. Hint.Resolve is Off, so no runtime Resolve<T>() exists and a
// missing binding is a build error rather than a startup one. Pure.DI is
// also used by Parrot.Core for the per-agent composition; each composition is
// compile-time generated and remains bounded to its assembly-owned graph.
//
// Three things stay hand-written on purpose. SlashCommandRegistry is a cycle
// (HelpCommand needs the registry holding it), clearer as four explicit lines
// than as a factory. The renderers take no dependencies at all, so routing them
// through a composition would add indirection and nothing else -- and the
// remote path needs one without ever building this graph. And a remote client
// is a channel, not a composed object: the server owns that side.
internal partial class Composition
{
    // Never called: Pure.DI reads it at compile time. Internal rather than
    // private so it is not an unused private member (IDE0051).
    internal static void Setup() =>
        DI.Setup(nameof(Composition))
            .Hint(Hint.Resolve, "Off")

            // Supplied when the composition is built: credentials are read and
            // the registry assembled asynchronously before this point, and
            // roots are synchronous. The registry resolves the provider per
            // session, so no single provider is bound here.
            .Arg<ProviderRegistry>("registry")
            .Arg<Configuration>("configuration")
            .Arg<string>("workingDirectory", "workingDirectory")
            .Arg<string>("hostKey", "hostKey")
            .Arg<IUserSessionHost>("userSessionHost")

            .Bind().As(Lifetime.Singleton).To(_ => StatePaths.ResolveFromEnvironment())
            .Bind().As(Lifetime.Singleton).To(ctx =>
            {
                ctx.Inject<StatePaths>(out var paths);
                return new SessionCatalog(paths);
            })
            .Bind().As(Lifetime.Singleton).To(ctx =>
            {
                ctx.Inject<Configuration>(out var configuration);
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                return new SkillCatalogFactory(
                    configuration,
                    home,
                    Path.Combine(AppContext.BaseDirectory, "skills"));
            })
            .Bind().As(Lifetime.Singleton).To(_ => ExecutableLocator.Capture())
            .Bind().As(Lifetime.Singleton).To(ctx =>
            {
                ctx.Inject<Configuration>(out var configuration);
                ctx.Inject<ExecutableLocator>(out var locator);
                return CliUtilityAvailability.Inspect(configuration.CliUtilities, locator);
            })
            .Bind().As(Lifetime.Singleton).To(ctx =>
            {
                ctx.Inject<ExecutableLocator>(out var locator);
                return ProcessRunner.Locate(locator);
            })

            .Bind().As(Lifetime.Singleton).To(ctx =>
            {
                ctx.Inject<Configuration>(out var configuration);
                IWebAddressPolicy policy = configuration.WebFetch.AllowPrivate
                    ? new PrivateWebAddressPolicy()
                    : new PublicWebAddressPolicy();
                return WebFetcher.Create(policy);
            })

            .Bind().As(Lifetime.Singleton).To(ctx =>
            {
                ctx.Inject<Configuration>(out var configuration);
                return new Compactor(
                    configuration.Compaction.TriggerPercent,
                    configuration.Compaction.TargetPercent,
                    configuration.Compaction.MaximumInputTokens,
                    configuration.Compaction.SummaryOutputTokens,
                    configuration.PromptTemplates);
            })
            .Bind().As(Lifetime.Singleton).To(ctx =>
            {
                ctx.Inject<ProviderRegistry>(out var registry);
                ctx.Inject<Configuration>(out var configuration);
                return new ModelAliasCatalog(
                    registry,
                    configuration.ModelAliases.Select(alias =>
                    {
                        var icon = alias.Value.Icon is null
                            ? null
                            : ModelAliasIcon.Parse(alias.Value.Icon.Glyph, alias.Value.Icon.Color);
                        return new ModelAliasDefinition(
                            alias.Key,
                            alias.Value.ModelString,
                            alias.Value.Usage,
                            alias.Value.AugmentSystemPrompt,
                            icon);
                    }));
            })
            .Bind().As(Lifetime.Singleton).To(ctx =>
            {
                ctx.Inject<ModelAliasCatalog>(out var aliases);
                ctx.Inject<Configuration>(out var configuration);
                return new ModelRouting(aliases, configuration.Model);
            })
            .Bind().As(Lifetime.Singleton).To(ctx =>
            {
                ctx.Inject<ProviderRegistry>(out var registry);
                ctx.Inject<ModelRouting>(out var routing);
                return new ModelRouter(registry, routing);
            })
            .Bind().As(Lifetime.Singleton).To(ctx =>
            {
                ctx.Inject<Configuration>(out var configuration);
                ctx.Inject<ModelRouting>(out var routing);
                ctx.Inject<ModelRouter>(out var router);
                return new ModelConfigurationCoordinator(configuration, routing, router);
            })
            .Bind().As(Lifetime.Singleton).To(ctx =>
            {
                ctx.Inject<ModelConfigurationCoordinator>(out var models);
                return new ModelAliasConfigurator(models);
            })
            .Bind().As(Lifetime.Singleton).To(ctx =>
            {
                ctx.Inject<Configuration>(out var configuration);
                return new ProfileRegistry(
                    configuration.Profiles,
                    configuration.SandboxRules,
                    [],
                    configuration.DisabledTools);
            })
            .Bind().As(Lifetime.Singleton).To<ISystemPromptProvider>(ctx =>
            {
                ctx.Inject<Configuration>(out var configuration);
                ctx.Inject<string>("workingDirectory", out var workingDirectory);
                ctx.Inject<StatePaths>(out var paths);
                ctx.Inject<ProfileRegistry>(out var profiles);
                ctx.Inject<CliUtilityAvailability>(out var cliUtilities);
                List<ISystemPromptProvider> systemPromptProviders =
                [
                    .. configuration.SystemPrompts.Select(
                        entry => new ConfiguredSystemPromptProvider(entry.Key, entry.Value)),
                    new AgentsPromptProvider(workingDirectory, paths.Config, configuration.PromptTemplates),
                    new ExpectedCliUtilitiesProvider(cliUtilities, configuration.PromptTemplates),
                    new PlatformProvider(configuration.PromptTemplates),
                    new WorkingDirectoryProvider(workingDirectory, configuration.PromptTemplates),
                    new GitRepositoryProvider(ProjectWorkspace.FromLaunchDirectory(Path.GetFullPath(workingDirectory)), configuration.PromptTemplates),
                    new OptionalCliUtilitiesProvider(cliUtilities, configuration.PromptTemplates),
                    new SessionIdentityProvider(),
                    new SubagentsProvider(profiles, configuration.PromptTemplates),
                    new ModelPromptProvider(configuration.ModelAugmentSystemPrompts, configuration.PromptTemplates),
                    new QueueGuidanceProvider(configuration.PromptTemplates),
                    new SecurityProfileProvider(configuration.SandboxRules, configuration.PromptTemplates),
                ];
                return new CompositeSystemPromptProvider("runtime:system-prompt", systemPromptProviders);
            })
            .Bind().As(Lifetime.Singleton).To(ctx =>
            {
                ctx.Inject<ProfileRegistry>(out var profiles);
                ctx.Inject<Configuration>(out var configuration);
                return new ModeRegistry(profiles, configuration.DefaultProfile);
            })

            // The static half of an agent session is bound into the source
            // here. The source mints one factory per user session, that factory
            // holds the user session's tool factories, and each of those yields
            // one tool instance per agent session -- so the only thing left to
            // pass per call is id, depth, and the session's own broker and
            // repository.
            .Bind().As(Lifetime.Singleton).To<IAgentSessionFactorySource>(ctx =>
            {
                ctx.Inject<ProcessRunner>(out var processes);
                ctx.Inject<Compactor>(out var compactor);
                ctx.Inject<WebFetcher>(out var webFetcher);
                ctx.Inject<Configuration>(out var configuration);
                ctx.Inject<ModelRouter>(out var router);
                ctx.Inject<ISystemPromptProvider>(out var systemPromptProvider);

                return new AgentSessionFactorySource(
                    processes,
                    compactor,
                    webFetcher,
                    configuration.ToolDefinitions,
                    configuration.AgentTasks,
                    configuration.ReadOnlyExecCommandPrefixes,
                    router,
                    systemPromptProvider,
                    configuration.PromptTemplates);
            })

            .Bind().As(Lifetime.Singleton).To<IUserSessionFactory>(ctx =>
            {
                ctx.Inject<IAgentSessionFactorySource>(out var agentSessionFactories);
                ctx.Inject<ModeRegistry>(out var modes);
                ctx.Inject<ProfileRegistry>(out var profiles);
                ctx.Inject<Configuration>(out var configuration);
                ctx.Inject<SkillCatalogFactory>(out var skillCatalogFactory);
                return new UserSessionFactory(
                    agentSessionFactories,
                    modes,
                    configuration.PromptTemplates,
                    profiles,
                    skillCatalogFactory,
                    configuration.UserInputTimeout,
                    TimeProvider.System);
            })

            .Bind().As(Lifetime.Singleton).To(ctx =>
            {
                ctx.Inject<StatePaths>(out var paths);
                ctx.Inject<IUserSessionFactory>(out var userSessions);
                ctx.Inject<ModelRouter>(out var router);
                ctx.Inject<ModeRegistry>(out var modes);
                ctx.Inject<string>("workingDirectory", out var workingDirectory);
                ctx.Inject<string>("hostKey", out var hostKey);
                return new SessionStore(paths, workingDirectory, hostKey, userSessions, router, modes);
            })

            .Bind().As(Lifetime.Singleton).To(ctx =>
            {
                ctx.Inject<ModelRouter>(out var router);
                ctx.Inject<ProviderRegistry>(out var registry);
                ctx.Inject<ModelAliasConfigurator>(out var aliases);
                ctx.Inject<ModelConfigurationCoordinator>(out var modelConfiguration);
                ctx.Inject<SessionStore>(out var store);
                ctx.Inject<SessionCatalog>(out var sessionCatalog);
                ctx.Inject<ModeRegistry>(out var modes);
                ctx.Inject<IUserSessionHost>(out var userSessionHost);
                return new ParrotService(router, registry, aliases, modelConfiguration, store, sessionCatalog, modes, userSessionHost);
            })

            .Root<StatePaths>("Paths")
            .Root<Configuration>("Configuration")
            .Root<CliUtilityAvailability>("CliUtilities")
            .Root<SessionStore>("Store")
            .Root<ParrotService>("Service");
}
