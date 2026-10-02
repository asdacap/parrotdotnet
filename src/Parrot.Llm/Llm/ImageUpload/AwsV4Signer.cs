using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Parrot.Llm.ImageUpload;

// Signs an S3 request with AWS Signature Version 4 in the Authorization header,
// single chunk. The host and every header already on the message are signed;
// content headers are not.
internal sealed class AwsV4Signer(string accessKey, string secretKey, string region)
{
    public const string EmptyPayloadSha256 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    private const string Algorithm = "AWS4-HMAC-SHA256";
    private const string Service = "s3";

    public void Sign(HttpRequestMessage message, string payloadSha256Hex, DateTimeOffset timestamp)
    {
        ArgumentNullException.ThrowIfNull(message);
        var uri = message.RequestUri ?? throw new ArgumentException("The request has no URI.", nameof(message));
        var amzDate = timestamp.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var date = amzDate[..8];
        _ = message.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
        _ = message.Headers.TryAddWithoutValidation("x-amz-content-sha256", payloadSha256Hex);

        var headers = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["host"] = uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}",
        };
        foreach (var header in message.Headers)
        {
            headers[header.Key.ToLowerInvariant()] = string.Join(",", header.Value.Select(static value => value.Trim()));
        }

        var signedHeaders = string.Join(";", headers.Keys);
        var canonicalHeaders = string.Concat(headers.Select(static header => $"{header.Key}:{header.Value}\n"));
        var canonicalRequest =
            $"{message.Method.Method}\n{CanonicalPath(uri)}\n{CanonicalQuery(uri)}\n{canonicalHeaders}\n{signedHeaders}\n{payloadSha256Hex}";
        var scope = $"{date}/{region}/{Service}/aws4_request";
        var stringToSign =
            $"{Algorithm}\n{amzDate}\n{scope}\n{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest)))}";
        var signingKey = Hmac(Hmac(Hmac(Hmac(Encoding.UTF8.GetBytes("AWS4" + secretKey), date), region), Service), "aws4_request");
        var signature = Convert.ToHexStringLower(Hmac(signingKey, stringToSign));
        _ = message.Headers.TryAddWithoutValidation(
            "Authorization",
            $"{Algorithm} Credential={accessKey}/{scope}, SignedHeaders={signedHeaders}, Signature={signature}");
    }

    private static byte[] Hmac(byte[] key, string data) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));

    private static string CanonicalPath(Uri uri) =>
        string.Join("/", uri.AbsolutePath.Split('/').Select(static segment => Encode(Uri.UnescapeDataString(segment))));

    private static string CanonicalQuery(Uri uri)
    {
        var pairs = uri.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(static pair =>
            {
                var separator = pair.IndexOf('=', StringComparison.Ordinal);
                var key = separator < 0 ? pair : pair[..separator];
                var value = separator < 0 ? string.Empty : pair[(separator + 1)..];
                return (Key: Encode(Uri.UnescapeDataString(key)), Value: Encode(Uri.UnescapeDataString(value)));
            })
            .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
            .ThenBy(static pair => pair.Value, StringComparer.Ordinal);
        return string.Join("&", pairs.Select(static pair => $"{pair.Key}={pair.Value}"));
    }

    // RFC 3986 unreserved characters pass; everything else is percent-encoded once.
    private static string Encode(string value)
    {
        var encoded = new StringBuilder(value.Length);
        foreach (var octet in Encoding.UTF8.GetBytes(value))
        {
            var character = (char)octet;
            if (char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or '~')
            {
                _ = encoded.Append(character);
            }
            else
            {
                _ = encoded.Append('%').Append(octet.ToString("X2", CultureInfo.InvariantCulture));
            }
        }

        return encoded.ToString();
    }
}
