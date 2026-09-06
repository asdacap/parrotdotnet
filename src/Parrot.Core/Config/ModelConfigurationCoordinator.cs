using Parrot.Llm;

namespace Parrot.Config;

internal sealed class ModelConfigurationCoordinator(
    Configuration configuration,
    ModelRouting routing,
    ModelRouter router)
{
    private readonly Lock _gate = Configuration.ModelConfigurationLock(configuration.ConfigurationPath);

    public ModelRoutingSnapshot CaptureRouting() => routing.Capture();

    public IReadOnlyDictionary<string, ModelPresetConfig> CapturePresets() => configuration.ModelPresets;

    public ModelAliasDefinition ConfigureAlias(string name, string modelString)
    {
        lock (_gate)
        {
            var refreshed = Reload();
            var existing = refreshed.ModelAliases.GetValueOrDefault(name)
                ?? throw new LLMProviderException($"model alias: alias \"{name}\" is not defined");
            var definitions = Definitions(refreshed.ModelAliases).Select(definition =>
                string.Equals(definition.Name, name, StringComparison.Ordinal)
                    ? definition with { ModelString = modelString }
                    : definition).ToArray();
            var replacement = routing.Prepare(refreshed.Model, definitions);

            refreshed.SetModelAlias(name, modelString);
            Configuration.SynchronizeModelConfiguration(configuration, refreshed);
            routing.Publish(replacement);
            return new ModelAliasDefinition(
                name,
                modelString,
                existing.Usage,
                existing.AugmentSystemPrompt,
                ParseIcon(existing.Icon));
        }
    }

    public IReadOnlyList<ProviderModelAliasDefaults> ListProviderDefaults(IEnumerable<string> providerIds)
    {
        ArgumentNullException.ThrowIfNull(providerIds);
        return
        [
            .. providerIds
                .Distinct(StringComparer.Ordinal)
                .Where(configuration.ProviderModelAliasDefaults.ContainsKey)
                .OrderBy(providerId => providerId, StringComparer.Ordinal)
                .Select(providerId => configuration.ProviderModelAliasDefaults[providerId]),
        ];
    }

    public IReadOnlyList<ModelAliasDefinition> ApplyProviderDefaults(string providerId)
    {
        lock (_gate)
        {
            var refreshed = Reload();
            var defaults = refreshed.ProviderModelAliasDefaults.GetValueOrDefault(providerId)
                ?? configuration.ProviderModelAliasDefaults.GetValueOrDefault(providerId)
                ?? throw new LLMProviderException(
                    $"model alias defaults: provider \"{providerId}\" is not configured");
            var targets = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["low_llm"] = defaults.LowModelString,
                ["medium_llm"] = defaults.MediumModelString,
                ["high_llm"] = defaults.HighModelString,
                ["xhigh_llm"] = defaults.XHighModelString,
            };
            var definitions = Definitions(refreshed.ModelAliases).Select(definition =>
                targets.TryGetValue(definition.Name, out var modelString)
                    ? definition with { ModelString = modelString }
                    : definition).ToArray();
            var replacement = routing.Prepare(refreshed.Model, definitions);

            refreshed.SetModelAliases(defaults);
            Configuration.SynchronizeModelConfiguration(configuration, refreshed);
            routing.Publish(replacement);
            return [.. definitions.Where(definition => targets.ContainsKey(definition.Name))];
        }
    }

    public ModelPresetConfig SetPreset(string name, string selectedModel)
    {
        lock (_gate)
        {
            Configuration.ValidatePresetName(name);
            var refreshed = Reload();
            _ = router.Resolve(selectedModel);
            var preset = new ModelPresetConfig(
                selectedModel,
                refreshed.ModelAliases.Select(alias =>
                    new KeyValuePair<string, string>(alias.Key, alias.Value.ModelString)));

            refreshed.SetModelPreset(name, preset);
            Configuration.SynchronizeModelConfiguration(configuration, refreshed);
            return preset;
        }
    }

    public ModelPresetSelection SelectPreset(
        string name,
        string selectedModel,
        Action<ResolvedModelSelection> applySelection)
    {
        ArgumentNullException.ThrowIfNull(applySelection);

        lock (_gate)
        {
            Configuration.ValidatePresetName(name);
            var refreshed = Reload();
            var previousConfiguration = configuration.ReloadModelConfiguration();
            var previousRouting = routing.Capture();
            var previousSelection = router.ResolveFrom(previousRouting, selectedModel);
            var preset = refreshed.ModelPresets.GetValueOrDefault(name)
                ?? throw new KeyNotFoundException($"model preset: preset \"{name}\" is not configured");
            var aliases = new SortedDictionary<string, ModelAliasConfig>(
                refreshed.ModelAliases.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal),
                StringComparer.Ordinal);
            foreach (var target in preset.ModelAliases)
            {
                if (!aliases.TryGetValue(target.Key, out var existing))
                {
                    throw new LLMProviderException(
                        $"model preset: alias \"{target.Key}\" from preset \"{name}\" is no longer defined");
                }

                aliases[target.Key] = existing with { ModelString = target.Value };
            }

            var replacement = routing.Prepare(preset.Model, Definitions(aliases));
            var resolved = router.ResolveFrom(replacement, preset.Model);
            refreshed.SetModelRouting(preset.Model, preset.ModelAliases);
            Configuration.SynchronizeModelConfiguration(configuration, refreshed);
            routing.Publish(replacement);
            try
            {
                applySelection(resolved);
            }
            catch
            {
                try
                {
                    applySelection(previousSelection);
                }
                finally
                {
                    RollbackPresetSelection(previousConfiguration, previousRouting);
                }

                throw;
            }

            return new ModelPresetSelection(preset, resolved);
        }
    }

    public ModelRoutingSnapshot Refresh()
    {
        lock (_gate)
        {
            return ReloadRouting();
        }
    }

    private static IEnumerable<ModelAliasDefinition> Definitions(
        IReadOnlyDictionary<string, ModelAliasConfig> aliases) =>
        aliases.Select(alias => new ModelAliasDefinition(
            alias.Key,
            alias.Value.ModelString,
            alias.Value.Usage,
            alias.Value.AugmentSystemPrompt,
            ParseIcon(alias.Value.Icon)));

    private static ModelAliasIcon? ParseIcon(ModelAliasIconConfig? icon) => icon is null
        ? null
        : ModelAliasIcon.Parse(icon.Glyph, icon.Color);

    private void RollbackPresetSelection(Configuration previousConfiguration, ModelRoutingSnapshot previousRouting)
    {
        lock (_gate)
        {
            previousConfiguration.SetModelRouting(
                previousConfiguration.Model,
                previousConfiguration.ModelAliases.ToDictionary(
                    entry => entry.Key,
                    entry => entry.Value.ModelString,
                    StringComparer.Ordinal));
            Configuration.SynchronizeModelConfiguration(configuration, previousConfiguration);
            routing.Publish(previousRouting);
        }
    }

    private Configuration Reload()
    {
        var refreshed = configuration.ReloadModelConfiguration();
        var replacement = routing.Prepare(refreshed.Model, Definitions(refreshed.ModelAliases));
        Configuration.SynchronizeModelConfiguration(configuration, refreshed);
        routing.Publish(replacement);
        return refreshed;
    }

    private ModelRoutingSnapshot ReloadRouting()
    {
        _ = Reload();
        return routing.Capture();
    }
}
