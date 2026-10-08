using Parrot.Agent;
using Parrot.AgentTasks;
using Parrot.Config;
using Parrot.Context;
using Parrot.Diagnostics;
using Parrot.Llm;
using Parrot.Process;
using Parrot.Skills;
using Parrot.State;
using Parrot.Store;
using Parrot.Web;

namespace Parrot.Core.Tests;

internal sealed class ProductionSessionStoreFixture : IDisposable
{
    private readonly DiagnosticLogs _diagnostics;

    public ProductionSessionStoreFixture(string directory, ProviderModel model, bool passThroughSandbox)
    {
        Paths = new StatePaths(Path.Combine(directory, "state"), Path.Combine(directory, "config"), Path.Combine(directory, "data"));
        _diagnostics = new DiagnosticLogs(Paths, FileDiagnosticLog.CreateInstanceId(), TextWriter.Null, TimeProvider.System);
        Configuration = Configuration.Load(Paths.ConfigFile, Paths.PredefinedConfigFile);
        Router = TestModels.Route(model);
        var profiles = new ProfileRegistry(Configuration.Profiles, Configuration.SandboxRules, [], Configuration.DisabledTools);
        Modes = new ModeRegistry(profiles, Configuration.DefaultProfile);
        var source = new AgentSessionFactorySource(
            passThroughSandbox && OperatingSystem.IsLinux()
                ? TestModels.Runner(SandboxPassThrough.Write(directory))
                : ProcessRunner.LocateConfigured(ExecutableLocator.Capture(), new SandboxGate(enabled: true), []),
            new Compactor(90, 30, 60_000, 1024, Configuration.PromptTemplates),
            WebFetcher.Create(new PublicWebAddressPolicy()),
            Configuration.ToolDefinitions,
            Configuration.AgentTasks,
            Configuration.AgentSend,
            Configuration.RequestLimits,
            Configuration.ReadOnlyExecCommandPrefixes,
            Router,
            [],
            Configuration.PromptTemplates,
            static (arguments, scope) => new AgentSessionComposition(arguments, scope));
        var factory = new UserSessionFactory(
            source,
            Modes,
            Configuration.PromptTemplates,
            profiles,
            new SkillCatalogFactory(Configuration, directory, Path.Combine(directory, "skills")),
            TimeSpan.FromSeconds(30),
            TimeProvider.System,
            AgentTaskParser.ParseArtifact);
        Store = new SessionStore(Paths, directory, "host", factory, Router, Modes, _diagnostics);
    }

    public StatePaths Paths { get; }

    public Configuration Configuration { get; }

    public IModelRouter Router { get; }

    public ModeRegistry Modes { get; }

    public SessionStore Store { get; }

    public void Dispose() => _diagnostics.Dispose();
}
