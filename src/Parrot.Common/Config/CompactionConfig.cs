namespace Parrot.Config;

internal sealed record CompactionConfig(
    int TriggerPercent,
    int TargetPercent,
    int MaximumInputTokens,
    int SummaryOutputTokens);
