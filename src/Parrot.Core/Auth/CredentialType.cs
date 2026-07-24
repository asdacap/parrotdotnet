using System.Text.Json.Serialization;

namespace Parrot.Auth;

[JsonConverter(typeof(JsonStringEnumConverter<CredentialType>))]
internal enum CredentialType
{
    [JsonStringEnumMemberName("api_key")]
    ApiKey,

    [JsonStringEnumMemberName("oauth")]
    OAuth,
}
