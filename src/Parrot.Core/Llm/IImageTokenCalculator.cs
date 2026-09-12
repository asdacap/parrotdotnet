namespace Parrot.Llm;

// Calculates local image input estimates using the provider's current wire preprocessing.
internal interface IImageTokenCalculator
{
    // Unknown models and unidentifiable images retain the conservative fallback estimate.
    long CalculateImageTokens(LLMModel model, LLMContent image);
}
