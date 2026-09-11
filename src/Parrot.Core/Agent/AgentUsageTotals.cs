namespace Parrot.Agent;

internal sealed record AgentUsageTotals(
    long InputTokens,
    long CachedInputTokens,
    long OutputTokens,
    long ToolCalls,
    double InputCost,
    double OutputCost)
{
    public static AgentUsageTotals Empty { get; } = new(0, 0, 0, 0, 0, 0);

    public double TotalCost => InputCost + OutputCost;

    public AgentUsageTotals Add(AgentUsageTotals increment) => new(
        checked(InputTokens + increment.InputTokens),
        checked(CachedInputTokens + increment.CachedInputTokens),
        checked(OutputTokens + increment.OutputTokens),
        checked(ToolCalls + increment.ToolCalls),
        InputCost + increment.InputCost,
        OutputCost + increment.OutputCost);
}
