namespace Parrot.Auth;

// Injectable knobs for the OpenAI OAuth client, so the flows are testable
// against a fake issuer and clock.
internal sealed record OpenAiOAuthOptions
{
    public string Issuer { get; init; } = OpenAiOAuthClient.DefaultIssuer;

    public Func<DateTimeOffset>? Clock { get; init; }

    public TimeSpan LoginTimeout { get; init; } = TimeSpan.FromMinutes(5);

    public TimeSpan PollingSafetyMargin { get; init; } = TimeSpan.FromSeconds(3);
}
