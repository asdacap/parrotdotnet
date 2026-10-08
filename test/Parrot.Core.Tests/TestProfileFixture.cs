using Parrot.Agent;

namespace Parrot.Core.Tests;

internal sealed class TestProfileFixture
{
    public TestProfileFixture() => Profile = Registry.ResolveChild("test");

    public ProfileRegistry Registry { get; } = new(TestModels.Profiles, [], [], new HashSet<string>(StringComparer.Ordinal));

    public IAgentProfile Profile { get; }
}
