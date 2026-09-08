namespace Parrot.Llm.Wire;

internal sealed record ImageGenerationWireRequest(string Prompt, ImageGenerationWireReference[]? Images)
{
    public string Model { get; } = "gpt-image-2";

    public string Background { get; } = "auto";

    public string Quality { get; } = "auto";

    public string Size { get; } = "auto";
}
