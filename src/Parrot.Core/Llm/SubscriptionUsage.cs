namespace Parrot.Llm;

// The quota information a subscription provider exposes.
internal sealed record SubscriptionUsage
{
    public string PlanType { get; init; } = string.Empty;

    public UsageWindow? PrimaryWindow { get; init; }

    public UsageWindow? SecondaryWindow { get; init; }

    public UsageCredits? Credits { get; init; }
}
