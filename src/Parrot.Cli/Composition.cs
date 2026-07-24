using Parrot.Agent;
using Parrot.Config;
using Parrot.Context;
using Parrot.Events;
using Parrot.Llm;
using Parrot.Process;
using Parrot.Protocol;
using Parrot.State;
using Parrot.Store;
using Parrot.Tools;
using Pure.DI;

namespace Parrot.Cli;

// The composition root, generated at compile time by Pure.DI: nested
// constructor calls, no container, no reflection, validated at build.
//
// AGENTS.md says "not IoC container"; this is a bounded exception, and the
// bounds are here. Hint.Resolve is Off, so no runtime Resolve<T>() exists and a
// missing binding is a build error rather than a startup one. Pure.DI is
// referenced only by Parrot.Cli -- Parrot.Core takes plain Func<> factories, so
// the domain gains no codegen dependency.
//
// Three things stay hand-written on purpose. SlashCommandRegistry is a cycle
// (HelpCommand needs the registry holding it), clearer as four explicit lines
// than as a factory. The renderers take no dependencies at all, so routing them
// through a composition would add indirection and nothing else -- and the
// remote path needs one without ever building this graph. And a remote client
// is a channel, not a composed object: the server owns that side.
internal partial class Composition
{
    private const string ProviderId = "opencode-go";
    private const string ProviderBaseUrl = "https://opencode.ai/zen/go/v1/";

    // Well under the smallest model window, with margin for the system context
    // and tool results the estimate does not see precisely.
    private const int CompactionTokenBudget = 120_000;

    // Never called: Pure.DI reads it at compile time. Internal rather than
    // private so it is not an unused private member (IDE0051).
    internal static void Setup() =>
        DI.Setup(nameof(Composition))
            .Hint(Hint.Resolve, "Off")

            // Supplied when the composition is built: the credential is read
            // asynchronously before this point, and roots are synchronous.
            .Arg<string>("apiKey", "apiKey")
            .Arg<string>("workingDirectory", "workingDirectory")
            .Arg<string>("hostKey", "hostKey")

            .Bind().As(Lifetime.Singleton).To(_ => new HttpClient())
            .Bind().As(Lifetime.Singleton).To(_ => StatePaths.ResolveFromEnvironment())
            .Bind().As(Lifetime.Singleton).To(_ => ProcessRunner.Locate())

            .Bind().As(Lifetime.Singleton).To(ctx =>
            {
                ctx.Inject<StatePaths>(out var paths);
                return Configuration.Load(paths.ConfigFile);
            })

            .Bind().As(Lifetime.Singleton).To<ILLMProvider>(ctx =>
            {
                ctx.Inject<HttpClient>(out var http);
                ctx.Inject<string>("apiKey", out var apiKey);
                return new OpenAICompatibleProvider(ProviderId, new Uri(ProviderBaseUrl), apiKey, http);
            })

            .Bind().As(Lifetime.Singleton).To(_ => new ToolRegistry(
                [new ExecCommandTool(), new ReadFileTool(), new AgentSpawnTool()]))

            .Bind().As(Lifetime.Singleton).To(ctx =>
            {
                ctx.Inject<string>("workingDirectory", out var workingDirectory);
                return new SystemContextBuilder(
                    workingDirectory,
                    DateTimeOffset.UtcNow.ToString(
                        "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
            })

            .Bind().As(Lifetime.Singleton).To(ctx =>
            {
                ctx.Inject<ILLMProvider>(out var provider);
                return new Compactor(provider, CompactionTokenBudget);
            })

            // The static half of an agent session is injected here; the
            // per-instance half -- id, depth, and the session's own broker and
            // repository -- arrives per call. This is what lets SessionStore
            // and UserSession stop relaying five parameters they never use.
            .Bind().To<Func<string, int, EventBroker, EventRepository, AgentSession>>(ctx =>
            {
                ctx.Inject<ILLMProvider>(out var provider);
                ctx.Inject<ToolRegistry>(out var tools);
                ctx.Inject<ProcessRunner>(out var processes);
                ctx.Inject<SystemContextBuilder>(out var systemContext);
                ctx.Inject<Compactor>(out var compactor);
                ctx.Inject<string>("workingDirectory", out var workingDirectory);

                return (sessionId, depth, broker, repository) => new AgentSession(
                    sessionId,
                    provider,
                    broker,
                    repository,
                    tools,
                    workingDirectory,
                    processes,
                    systemContext,
                    compactor,
                    depth);
            })

            .Bind().To<Func<string, string, EventRepository, Parrot.Agent.UserSession>>(ctx =>
            {
                ctx.Inject<Func<string, int, EventBroker, EventRepository, AgentSession>>(out var newAgent);
                return (id, model, repository) => new Parrot.Agent.UserSession(id, model, repository, newAgent);
            })

            .Bind().As(Lifetime.Singleton).To(ctx =>
            {
                ctx.Inject<StatePaths>(out var paths);
                ctx.Inject<Func<string, string, EventRepository, Parrot.Agent.UserSession>>(out var newUserSession);
                ctx.Inject<string>("workingDirectory", out var workingDirectory);
                ctx.Inject<string>("hostKey", out var hostKey);
                return new SessionStore(paths.State, workingDirectory, hostKey, newUserSession);
            })

            .Bind().As(Lifetime.Singleton).To(ctx =>
            {
                ctx.Inject<ILLMProvider>(out var provider);
                ctx.Inject<SessionStore>(out var store);
                return new ParrotService(provider, store);
            })

            .Root<StatePaths>("Paths")
            .Root<Configuration>("Configuration")
            .Root<SessionStore>("Store")
            .Root<ParrotService>("Service");
}
