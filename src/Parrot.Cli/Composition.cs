using Parrot.Agent;
using Parrot.Config;
using Parrot.Context;
using Parrot.Llm;
using Parrot.Process;
using Parrot.Protocol;
using Parrot.Security;
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
// referenced only by Parrot.Cli -- Parrot.Core takes factory interfaces it owns
// and declares, so the domain gains no codegen dependency.
//
// Three things stay hand-written on purpose. SlashCommandRegistry is a cycle
// (HelpCommand needs the registry holding it), clearer as four explicit lines
// than as a factory. The renderers take no dependencies at all, so routing them
// through a composition would add indirection and nothing else -- and the
// remote path needs one without ever building this graph. And a remote client
// is a channel, not a composed object: the server owns that side.
internal partial class Composition
{
    // Well under the smallest model window, with margin for the system context
    // and tool results the estimate does not see precisely.
    private const int CompactionTokenBudget = 120_000;

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

            .Bind().As(Lifetime.Singleton).To(_ => StatePaths.ResolveFromEnvironment())
            .Bind().As(Lifetime.Singleton).To(ctx =>
            {
                ctx.Inject<StatePaths>(out var paths);
                return new SessionCatalog(paths);
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

            .Bind("date").As(Lifetime.Singleton).To(_ =>
                DateTimeOffset.UtcNow.ToString(
                    "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture))

            .Bind().As(Lifetime.Singleton).To(_ => new Compactor(CompactionTokenBudget))
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
                ctx.Inject<ProviderRegistry>(out var registry);
                ctx.Inject<ModelAliasCatalog>(out var aliases);
                ctx.Inject<Configuration>(out var configuration);
                return new ModelRouter(registry, aliases, configuration.Model);
            })
            .Bind().As(Lifetime.Singleton).To(ctx =>
            {
                ctx.Inject<Configuration>(out var configuration);
                ctx.Inject<ModelAliasCatalog>(out var aliases);
                return new ModelAliasConfigurator(configuration, aliases);
            })
            .Bind().As(Lifetime.Singleton).To(ctx =>
            {
                ctx.Inject<Configuration>(out var configuration);
                ctx.Inject<StatePaths>(out var paths);
                return new ProfileRegistry(
                    configuration.Profiles,
                    configuration.SandboxRules,
                    new ApplicationDataSecurityRules(paths).Rules,
                    configuration.DisabledTools);
            })
            .Bind().As(Lifetime.Singleton).To<ISystemPromptProvider>(ctx =>
            {
                ctx.Inject<Configuration>(out var configuration);
                ctx.Inject<string>("workingDirectory", out var workingDirectory);
                ctx.Inject<StatePaths>(out var paths);
                ctx.Inject<string>("date", out var date);
                ctx.Inject<ProfileRegistry>(out var profiles);
                ctx.Inject<CliUtilityAvailability>(out var cliUtilities);
                return new CompositeSystemPromptProvider(
                    "runtime:system-prompt",
                    [
                        new BasePromptProvider(configuration.Prompt),
                        new AgentsPromptProvider(workingDirectory, paths.Config),
                        new ExpectedCliUtilitiesProvider(cliUtilities),
                        new DateProvider(date),
                        new PlatformProvider(),
                        new WorkingDirectoryProvider(workingDirectory),
                        new OptionalCliUtilitiesProvider(cliUtilities),
                        new SessionIdentityProvider(),
                        new SubagentsProvider(profiles),
                        new ModelPromptProvider(configuration.ModelAugmentSystemPrompts),
                        new QueueGuidanceProvider(),
                    ]);
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
                ctx.Inject<ModelRouter>(out var router);
                ctx.Inject<ISystemPromptProvider>(out var systemPromptProvider);
                ctx.Inject<IAgentSessionScopeFactory>(out var scopes);

                return new AgentSessionFactorySource(
                    processes,
                    compactor,
                    webFetcher,
                    router,
                    systemPromptProvider,
                    scopes);
            })

            .Bind().As(Lifetime.Singleton).To<IAgentSessionScopeFactory>(_ => new AgentSessionScopeFactory())

            .Bind().As(Lifetime.Singleton).To<IUserSessionFactory>(ctx =>
            {
                ctx.Inject<IAgentSessionFactorySource>(out var agentSessionFactories);
                ctx.Inject<ModeRegistry>(out var modes);
                ctx.Inject<ProfileRegistry>(out var profiles);
                ctx.Inject<Configuration>(out var configuration);
                return new UserSessionFactory(
                    agentSessionFactories,
                    modes,
                    profiles,
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
                ctx.Inject<SessionStore>(out var store);
                ctx.Inject<SessionCatalog>(out var sessionCatalog);
                ctx.Inject<ModeRegistry>(out var modes);
                return new ParrotService(router, registry, aliases, store, sessionCatalog, modes);
            })

            .Root<StatePaths>("Paths")
            .Root<Configuration>("Configuration")
            .Root<CliUtilityAvailability>("CliUtilities")
            .Root<SessionStore>("Store")
            .Root<ParrotService>("Service");
}
