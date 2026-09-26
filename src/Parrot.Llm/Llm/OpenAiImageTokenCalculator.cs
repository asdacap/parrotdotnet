namespace Parrot.Llm;

// Auto-detail rules: https://developers.openai.com/api/docs/guides/images-vision (2026-09-12).
internal sealed class OpenAiImageTokenCalculator : IImageTokenCalculator
{
    private static readonly Dictionary<string, ImageTokenRule> Rules =
        new(StringComparer.Ordinal)
        {
            ["gpt-6-astra"] = new(65535, 0, 120, 0, 0),
            ["gpt-5.6-sol"] = new(65535, 0, 120, 0, 0),
            ["gpt-5.6-terra"] = new(65535, 0, 120, 0, 0),
            ["gpt-5.6-luna"] = new(65535, 0, 120, 0, 0),
            ["gpt-5.5"] = new(6000, 10000, 120, 0, 0),
            ["gpt-5.4"] = new(2048, 2500, 120, 0, 0),
            ["gpt-5.4-mini"] = new(2048, 2500, 120, 0, 0),
            ["gpt-5.4-nano"] = new(2048, 2500, 120, 0, 0),
            ["gpt-5.2"] = new(2048, 6144, 120, 0, 0),
            ["gpt-4.1-mini"] = new(2048, 6144, 162, 0, 0),
            ["gpt-4.1-mini-2025-04-14"] = new(2048, 6144, 162, 0, 0),
            ["gpt-5.1"] = new(2048, 0, 0, 70, 140),
            ["gpt-4.1"] = new(2048, 0, 0, 85, 170),
            ["gpt-4o"] = new(2048, 0, 0, 85, 170),
            ["gpt-4o-mini"] = new(2048, 0, 0, 2833, 5667),
        };

    public static IImageTokenCalculator Instance { get; } = new OpenAiImageTokenCalculator();

    public long CalculateImageTokens(LLMModel model, LLMContent image)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(image);
        return !Rules.TryGetValue(model.Id, out var rule)
            || image.Kind != LLMContentKind.Image
            || image.ImageWidth <= 0
            || image.ImageHeight <= 0
                ? FallbackImageTokenCalculator.ImageTokens
                : rule.CalculateTokens(image.ImageWidth, image.ImageHeight);
    }

    private sealed record ImageTokenRule(int MaximumDimension, int PatchBudget, int MultiplierPercent, int BaseTokens, int TileTokens)
    {
        public long CalculateTokens(int width, int height)
        {
            var scale = Math.Min(1d, (double)MaximumDimension / Math.Max(width, height));
            width = Math.Max(1, (int)Math.Floor(width * scale));
            height = Math.Max(1, (int)Math.Floor(height * scale));
            if (TileTokens > 0)
            {
                scale = Math.Min(1d, 768d / Math.Min(width, height));
                width = Math.Max(1, (int)Math.Floor(width * scale));
                height = Math.Max(1, (int)Math.Floor(height * scale));
                return BaseTokens + ((width + 511L) / 512 * ((height + 511L) / 512) * TileTokens);
            }

            if (PatchBudget > 0 && CountPatches(width, height) > PatchBudget)
            {
                scale = Math.Sqrt(1024d * PatchBudget / ((double)width * height));
                scale *= Math.Min(
                    Math.Floor(width * scale / 32) / (width * scale / 32),
                    Math.Floor(height * scale / 32) / (height * scale / 32));
                width = Math.Max(1, (int)Math.Floor(width * scale));
                height = Math.Max(1, (int)Math.Floor(height * scale));
            }

            return ((CountPatches(width, height) * MultiplierPercent) + 99) / 100;
        }

        private static long CountPatches(int width, int height) => (width + 31L) / 32 * ((height + 31L) / 32);
    }
}
