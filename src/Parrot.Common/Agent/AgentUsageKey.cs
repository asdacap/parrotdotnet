using Parrot.Llm;

namespace Parrot.Agent;

internal sealed record AgentUsageKey(string Provider, string Model, string? Effort)
{
    public static AgentUsageKey Legacy { get; } = new("unknown", "legacy", null);

    public static AgentUsageKey FromRequest(ProviderModel model, ReasoningOptions? reasoning) =>
        new(model.Provider.Id, model.ModelId, reasoning?.Effort);
}
