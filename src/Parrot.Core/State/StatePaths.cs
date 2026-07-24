namespace Parrot.State;

internal sealed class StatePaths(string state, string config, string data)
{
    public string State { get; } = state;

    public string Config { get; } = config;

    public string Data { get; } = data;

    public string ConfigFile => Path.Combine(Config, "config.yaml");

    public string CredentialsFile => Path.Combine(Config, "credentials.json");

    public static StatePaths Resolve(IReadOnlyDictionary<string, string> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        var home = Lookup(environment, "HOME");

        return new StatePaths(
            Path.Combine(Fallback(Lookup(environment, "XDG_STATE_HOME"), home, ".local", "state"), "parrotdotnet"),
            Path.Combine(Fallback(Lookup(environment, "XDG_CONFIG_HOME"), home, ".config"), "parrotdotnet"),
            Path.Combine(Fallback(Lookup(environment, "XDG_DATA_HOME"), home, ".local", "share"), "parrotdotnet"));
    }

    public static StatePaths ResolveFromEnvironment()
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && entry.Value is string value)
            {
                environment[key] = value;
            }
        }

        return Resolve(environment);
    }

    private static string Lookup(IReadOnlyDictionary<string, string> environment, string name) =>
        environment.TryGetValue(name, out var value) ? value : string.Empty;

    private static string Fallback(string preferred, string home, params string[] relative) =>
        preferred.Length > 0 ? preferred : Path.Combine([home, .. relative]);
}
