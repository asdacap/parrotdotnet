using System.Text.Json.Serialization;

namespace Parrot.Auth;

internal sealed record S3Credential
{
    public const string ImageUploadName = "image_upload";

    [JsonPropertyName("access_key")]
    public Secret AccessKey { get; init; }

    [JsonPropertyName("secret_key")]
    public Secret SecretKey { get; init; }
}
