namespace Parrot.Config;

internal sealed class ModelProfiles(IReadOnlyDictionary<string, ModelProfileConfig> profiles)
{
    private readonly Dictionary<string, ModelProfileConfig> _profiles = new(profiles, StringComparer.Ordinal);

    public ModelProfileConfig Resolve(string canonicalSelector)
    {
        ArgumentNullException.ThrowIfNull(canonicalSelector);
        string? usage = null;
        string? systemPrompt = null;
        var prefix = canonicalSelector;

        while (prefix.Length > 0)
        {
            if (_profiles.TryGetValue(prefix, out var profile))
            {
                usage ??= profile.Usage;
                systemPrompt ??= profile.SystemPrompt;
                if (usage is not null && systemPrompt is not null)
                {
                    break;
                }
            }

            var boundary = prefix.LastIndexOf('/');
            if (boundary < 0)
            {
                break;
            }

            prefix = prefix[..boundary];
        }

        return new(usage, systemPrompt);
    }
}
