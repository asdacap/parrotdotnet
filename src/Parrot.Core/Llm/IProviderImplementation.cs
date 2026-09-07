using Parrot.Config;

namespace Parrot.Llm;

// Builds providers and their local model seeds from configuration and borrowed runtime dependencies.
internal interface IProviderImplementation
{
    // Checks whether required endpoint configuration is present, not whether credentials are valid.
    bool CanBuild(ProviderConfig config);

    BuiltProvider Build(ProviderBuildContext context);
}
