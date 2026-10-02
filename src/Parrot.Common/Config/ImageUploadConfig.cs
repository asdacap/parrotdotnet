namespace Parrot.Config;

// An S3-compatible bucket that receives prompt images so providers fetch a
// public URL instead of inline bytes. Credential values never live here;
// the credential store supplies the keys, with the *Env fields naming fallback environment variables.
internal sealed record ImageUploadConfig
{
    public required string Endpoint { get; init; }

    public required string Bucket { get; init; }

    public string Region { get; init; } = "us-east-1";

    public string KeyPrefix { get; init; } = "parrot/";

    // Base of the URL handed to providers; empty uses Endpoint.
    public string PublicBaseUrl { get; init; } = string.Empty;

    public string AccessKeyEnv { get; init; } = string.Empty;

    public string SecretKeyEnv { get; init; } = string.Empty;

    public int ExpiryDays { get; init; } = 1;
}
