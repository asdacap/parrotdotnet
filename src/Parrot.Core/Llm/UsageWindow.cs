namespace Parrot.Llm;

// A subscription rate-limit window. UsedPercent is the amount already used,
// 0 through 100.
internal sealed record UsageWindow(double UsedPercent, DateTimeOffset ResetAt, long LimitWindowSeconds);
