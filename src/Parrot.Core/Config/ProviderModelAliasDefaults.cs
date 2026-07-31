namespace Parrot.Config;

internal sealed record ProviderModelAliasDefaults(
    string ProviderId,
    string LowModelString,
    string MediumModelString,
    string HighModelString,
    string XHighModelString);
