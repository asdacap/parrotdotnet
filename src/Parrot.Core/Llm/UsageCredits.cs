namespace Parrot.Llm;

// Account balance or credit information, for subscriptions billed against a
// balance rather than rate-limit windows.
internal sealed record UsageCredits(bool HasCredits, string Balance);
