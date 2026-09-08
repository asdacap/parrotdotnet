namespace Parrot.Llm;

internal sealed record ImageGenerationRequest(string Prompt, IReadOnlyList<ImageGenerationReference> References);
