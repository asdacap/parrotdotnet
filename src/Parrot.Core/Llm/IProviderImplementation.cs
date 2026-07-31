using Parrot.Config;

namespace Parrot.Llm;

internal interface IProviderImplementation
{
    bool CanBuild(ProviderConfig config);

    BuiltProvider Build(ProviderBuildContext context);
}
