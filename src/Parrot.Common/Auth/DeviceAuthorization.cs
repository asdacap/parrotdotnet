namespace Parrot.Auth;

// A pending device-code authorization. The codes redact themselves in tostring.
internal sealed record DeviceAuthorization
{
    public Secret DeviceAuthId { get; init; }

    public Secret UserCode { get; init; }

    public string VerificationUrl { get; init; } = string.Empty;

    public TimeSpan Interval { get; init; }

    public DateTimeOffset ExpiresAt { get; init; }

    public override string ToString() => $"device authorization (url {VerificationUrl}, codes [REDACTED])";
}
