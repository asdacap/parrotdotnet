namespace Parrot.Llm;

internal sealed record ProviderModel
{
    public ProviderModel(ILLMProvider provider, LLMModel model)
        : this(provider, model, null)
    {
    }

    public ProviderModel(ILLMProvider provider, LLMModel model, ModelVariant? variant)
    {
        Provider = provider;
        Model = model;
        Variant = variant;
    }

    public ILLMProvider Provider { get; }

    public LLMModel Model { get; }

    public ModelVariant? Variant { get; }

    public string Selector => Variant is null
        ? $"{Provider.Id}/{Model.Id}"
        : $"{Provider.Id}/{Model.Id}/{Variant.Name}";

    public string ModelId => Model.Id;

    public ReasoningOptions? Reasoning => Variant is null
        ? null
        : new ReasoningOptions(Variant.ReasoningEffort, "auto");
}
