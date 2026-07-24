namespace Parrot.Llm;

// An optional provider capability: report subscription quota or account balance.
// A provider whose account exposes no usage simply does not implement this.
internal interface IUsageReporter
{
    Task<SubscriptionUsage> Usage(CancellationToken cancellationToken);
}
