namespace Parrot.Llm;

// Parses a provider's raw model-list response into models. The decoded models
// describe only what the catalogue reports; declared and preset metadata is
// overlaid separately by the merge step.
internal interface IModelListDecoder
{
    IReadOnlyList<LLMModel> Decode(string providerId, string json);
}
