using Parrot.Agent;

namespace Parrot.Core.Tests;

internal sealed class TestProfileFixture
{
    public TestProfileFixture()
    {
        var profile = Registry.ResolveChild("test");
        Mode = new NoopMode(profile, profile.SecurityProfile);
    }

    public ProfileRegistry Registry { get; } = new(TestModels.Profiles, [], [], new HashSet<string>(StringComparer.Ordinal));

    public IMode Mode { get; }
}
