namespace Parrot.Config;

internal sealed record ModelProfileConfig(
    string? Usage,
    string? SystemPrompt,
    int? MaximumInputTokens,
    int? SummaryOutputTokens);
