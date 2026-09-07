namespace Parrot.Llm;

// An optional provider capability: report subscription quota or account balance.
// Providers expose this separately, or null when usage is unsupported.
internal interface IUsageReporter
{
    Task<SubscriptionUsage> Usage(CancellationToken cancellationToken);
}
