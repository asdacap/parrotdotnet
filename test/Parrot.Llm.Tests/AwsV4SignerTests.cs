using System.Security.Cryptography;
using System.Text;
using Parrot.Llm.ImageUpload;

namespace Parrot.Core.Tests;

// Known-answer vectors from the AWS "Examples: Signature Calculations in AWS
// Signature Version 4" page for Amazon S3.
internal sealed class AwsV4SignerTests
{
    private static readonly DateTimeOffset Timestamp = new(2013, 5, 24, 0, 0, 0, TimeSpan.Zero);

    [Test]
    [Arguments(
        "GET",
        "https://examplebucket.s3.amazonaws.com/test.txt",
        "Range: bytes=0-9",
        "",
        "host;range;x-amz-content-sha256;x-amz-date",
        "f0e8bdb87c964420e857bd35b5d6ed310bd44f0170aba48dd91039c6036bdb41")]
    [Arguments(
        "PUT",
        "https://examplebucket.s3.amazonaws.com/test$file.text",
        "Date: Fri, 24 May 2013 00:00:00 GMT|x-amz-storage-class: REDUCED_REDUNDANCY",
        "Welcome to Amazon S3.",
        "date;host;x-amz-content-sha256;x-amz-date;x-amz-storage-class",
        "98ad721746da40c64f1a55b78f14c238d841ea1380cd77a1b5971af0ece108bd")]
    [Arguments(
        "GET",
        "https://examplebucket.s3.amazonaws.com/?lifecycle",
        "",
        "",
        "host;x-amz-content-sha256;x-amz-date",
        "fea454ca298b7da1c68078a5d1bdbfbbe0d65c699e0f91ac7a200a0136783543")]
    public async Task Signs_the_published_s3_examples(
        string method, string url, string headers, string body, string expectedSignedHeaders, string expectedSignature)
    {
        var signer = new AwsV4Signer("AKIAIOSFODNN7EXAMPLE", "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY", "us-east-1");
        var payloadHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body)));
        using var message = new HttpRequestMessage(new HttpMethod(method), new Uri(url));
        foreach (var header in headers.Split('|', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = header.IndexOf(": ", StringComparison.Ordinal);
            _ = message.Headers.TryAddWithoutValidation(header[..separator], header[(separator + 2)..]);
        }

        signer.Sign(message, payloadHash, Timestamp);

        _ = await Assert.That(string.Join(",", message.Headers.GetValues("Authorization"))).IsEqualTo(
            "AWS4-HMAC-SHA256 Credential=AKIAIOSFODNN7EXAMPLE/20130524/us-east-1/s3/aws4_request, "
            + $"SignedHeaders={expectedSignedHeaders}, Signature={expectedSignature}");
        _ = await Assert.That(string.Join(",", message.Headers.GetValues("x-amz-date"))).IsEqualTo("20130524T000000Z");
        _ = await Assert.That(string.Join(",", message.Headers.GetValues("x-amz-content-sha256"))).IsEqualTo(payloadHash);
    }
}
