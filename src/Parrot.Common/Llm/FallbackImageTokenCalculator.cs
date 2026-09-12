namespace Parrot.Llm;

internal sealed class FallbackImageTokenCalculator : IImageTokenCalculator
{
    public const long ImageTokens = 4096;

    public static IImageTokenCalculator Instance { get; } = new FallbackImageTokenCalculator();

    public long CalculateImageTokens(LLMModel model, LLMContent image) => ImageTokens;
}
