using Parrot.Config;
using Parrot.Context;
using Parrot.Process;
using Parrot.Store;

namespace Parrot.Core.Tests;

internal sealed class TestSystemPromptFixture(string workingDirectory, string configDirectory)
{
    public ISystemPromptProvider Provider { get; } = new CompositeSystemPromptProvider(
        "test:system-prompt",
        [
            new ConfiguredSystemPromptProvider("runtime:system-context:01-base", "Test base prompt."),
            new AgentsPromptProvider(workingDirectory, configDirectory, TestModels.PromptTemplates),
            new ExpectedCliUtilitiesProvider(EmptyCliUtilities(), TestModels.PromptTemplates),
            new PlatformProvider(TestModels.PromptTemplates),
            new WorkingDirectoryProvider(workingDirectory, TestModels.PromptTemplates),
            new GitRepositoryProvider(ProjectWorkspace.FromLaunchDirectory(Path.GetFullPath(workingDirectory)), TestModels.PromptTemplates),
            new OptionalCliUtilitiesProvider(EmptyCliUtilities(), TestModels.PromptTemplates),
            new SessionIdentityProvider(),
            new SubagentsProvider(new TestProfileFixture().Registry, TestModels.PromptTemplates),
            new ModelPromptProvider(new Dictionary<string, string>(StringComparer.Ordinal), TestModels.PromptTemplates),
            new SecurityProfileProvider([], new SandboxGate(enabled: true), TestModels.PromptTemplates),
        ]);

    private static CliUtilityAvailability EmptyCliUtilities() =>
        CliUtilityAvailability.Inspect(
            new CliUtilityCandidates([], []),
            new ExecutableLocator(string.Empty, string.Empty));
}
