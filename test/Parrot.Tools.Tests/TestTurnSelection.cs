using Parrot.Agent;
using Parrot.Llm;
using Parrot.Security;

namespace Parrot.Core.Tests;

internal static class TestTurnSelection
{
    public static AgentTurnSelection Create(SecurityProfile securityProfile)
    {
        var model = new ProviderModel(new UnusedProvider(), new LLMModel("model", "unused"));
        return new AgentTurnSelection(
            new ModelSelector(model.Selector),
            TestModels.Resolve(model),
            new TestProfileFixture().Profile,
            securityProfile);
    }
}
