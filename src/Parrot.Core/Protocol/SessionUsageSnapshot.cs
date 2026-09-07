using Parrot.Store;

namespace Parrot.Protocol;

public sealed partial class SessionUsageSnapshot
{
    internal static SessionUsageSnapshot From(SessionUsage usage) =>
        new()
        {
            Revision = usage.Revision,
            InputTokens = usage.InputTokens,
            CachedInputTokens = usage.CachedInputTokens,
            OutputTokens = usage.OutputTokens,
            ContextSize = usage.ContextSize,
            ContextLimit = usage.ContextLimit,
            InputCost = usage.InputCost,
            OutputCost = usage.OutputCost,
        };
}
