using Parrot.State;

namespace Parrot.Core.Tests;

internal sealed class StatePathsTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Resolve_uses_the_parrotdotnet_application_directory(bool useXdgHomes)
    {
        const string home = "/home/test";
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["HOME"] = home,
        };

        if (useXdgHomes)
        {
            environment["XDG_STATE_HOME"] = "/xdg/state";
            environment["XDG_CONFIG_HOME"] = "/xdg/config";
            environment["XDG_DATA_HOME"] = "/xdg/data";
        }

        var stateHome = useXdgHomes ? "/xdg/state" : Path.Combine(home, ".local", "state");
        var configHome = useXdgHomes ? "/xdg/config" : Path.Combine(home, ".config");
        var dataHome = useXdgHomes ? "/xdg/data" : Path.Combine(home, ".local", "share");
        var paths = StatePaths.Resolve(environment);

        _ = await Assert.That(paths.State).IsEqualTo(Path.Combine(stateHome, "parrotdotnet"));
        _ = await Assert.That(paths.Config).IsEqualTo(Path.Combine(configHome, "parrotdotnet"));
        _ = await Assert.That(paths.Data).IsEqualTo(Path.Combine(dataHome, "parrotdotnet"));
        _ = await Assert.That(paths.ConfigFile).IsEqualTo(Path.Combine(configHome, "parrotdotnet", "config.yaml"));
        _ = await Assert.That(paths.CredentialsFile)
            .IsEqualTo(Path.Combine(configHome, "parrotdotnet", "credentials.json"));
    }
}
