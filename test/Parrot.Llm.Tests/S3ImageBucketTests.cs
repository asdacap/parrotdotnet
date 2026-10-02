using System.Net;
using System.Security.Cryptography;
using System.Text;
using Parrot.Auth;
using Parrot.Config;
using Parrot.Llm;
using Parrot.Llm.ImageUpload;

namespace Parrot.Core.Tests;

internal sealed class S3ImageBucketTests
{
    private static readonly string AccessKeyEnv = SetVariable("PARROT_TEST_S3_ACCESS_KEY", "ak");
    private static readonly string SecretKeyEnv = SetVariable("PARROT_TEST_S3_SECRET_KEY", "sk");
    private static readonly byte[] Image = Convert.FromHexString("000102");
    private static readonly string ImageHash = Convert.ToHexStringLower(SHA256.HashData(Image));

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Prepare_creates_a_missing_bucket_then_installs_lifecycle_and_policy_once(bool exists, CancellationToken cancellationToken)
    {
        using var handler = new RecordingHttpHandler(request => Respond(
            request is { Method: "HEAD" } && !exists ? HttpStatusCode.NotFound : HttpStatusCode.OK));
        using var client = new HttpClient(handler, disposeHandler: false);
        var bucket = Bucket(client, string.Empty);

        await bucket.Prepare(cancellationToken);
        await bucket.Prepare(cancellationToken);
        var first = handler.Requests.Select(request => $"{request.Method} {request.Uri.PathAndQuery}").ToList();
        bucket.ForgetPreparation();
        await bucket.Prepare(cancellationToken);

        string[] expected = exists
            ? ["HEAD /b", "PUT /b?lifecycle", "PUT /b?policy"]
            : ["HEAD /b", "PUT /b", "PUT /b?lifecycle", "PUT /b?policy"];
        _ = await Assert.That(first).IsEquivalentTo(expected);
        _ = await Assert.That(handler.Requests.Count).IsEqualTo(expected.Length * 2);
        var lifecycle = handler.Requests.First(request => request.Uri.Query == "?lifecycle");
        _ = await Assert.That(lifecycle.Body).IsEqualTo(
            "<LifecycleConfiguration><Rule><ID>parrot-image-expiry</ID><Filter><Prefix>parrot/</Prefix></Filter>"
            + "<Status>Enabled</Status><Expiration><Days>3</Days></Expiration></Rule></LifecycleConfiguration>");
        _ = await Assert.That(lifecycle.Header("x-amz-checksum-sha256")).IsEqualTo(
            Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(lifecycle.Body))));
        _ = await Assert.That(lifecycle.Header("Authorization")).Contains(
            "SignedHeaders=host;x-amz-checksum-sha256;x-amz-content-sha256;x-amz-date;x-amz-sdk-checksum-algorithm,");
        var policy = handler.Requests.First(request => request.Uri.Query == "?policy");
        _ = await Assert.That(policy.Body).IsEqualTo(
            "{\"Version\":\"2012-10-17\",\"Statement\":[{\"Sid\":\"parrot-public-read\",\"Effect\":\"Allow\","
            + "\"Principal\":{\"AWS\":[\"*\"]},\"Action\":[\"s3:GetObject\"],\"Resource\":[\"arn:aws:s3:::b/parrot/*\"]}]}");
        _ = await Assert.That(handler.Requests.All(request =>
            request.Header("Authorization").StartsWith("AWS4-HMAC-SHA256 Credential=ak/19700101/us-east-1/s3/aws4_request, SignedHeaders=host;", StringComparison.Ordinal)
            && request.Header("x-amz-date") == "19700101T000000Z")).IsTrue();
    }

    [Test]
    [Arguments("image/png", ".png", "", "https://minio.example.com:9000")]
    [Arguments("image/jpeg", ".jpg", "https://cdn.example.com/", "https://cdn.example.com")]
    [Arguments("image/gif", ".gif", "", "https://minio.example.com:9000")]
    [Arguments("image/webp", ".webp", "https://cdn.example.com", "https://cdn.example.com")]
    public async Task Put_image_uses_a_content_addressed_key_and_the_public_base(
        string mediaType, string extension, string publicBaseUrl, string expectedBase, CancellationToken cancellationToken)
    {
        using var handler = new RecordingHttpHandler(static _ => Respond(HttpStatusCode.OK));
        using var client = new HttpClient(handler, disposeHandler: false);

        var url = await Bucket(client, publicBaseUrl).PutImage(Image, mediaType, cancellationToken);

        var request = handler.Requests.Single();
        _ = await Assert.That(url).IsEqualTo($"{expectedBase}/b/parrot/{ImageHash}{extension}");
        _ = await Assert.That($"{request.Method} {request.Uri}").IsEqualTo($"PUT https://minio.example.com:9000/b/parrot/{ImageHash}{extension}");
        _ = await Assert.That(request.Header("Content-Type")).IsEqualTo(mediaType);
        _ = await Assert.That(request.Header("x-amz-content-sha256")).IsEqualTo(ImageHash);
        _ = await Assert.That(request.Body).IsEqualTo(Encoding.UTF8.GetString(Image));
    }

    [Test]
    [Arguments("HEAD /b", 403, false)]
    [Arguments("PUT /b?lifecycle", 500, false)]
    [Arguments("PUT /b?policy", 403, false)]
    [Arguments("PUT /b/parrot/", 403, true)]
    public async Task Failures_are_permanent_provider_errors(string failing, int status, bool upload, CancellationToken cancellationToken)
    {
        using var handler = new RecordingHttpHandler(request =>
            $"{request.Method} {request.Uri.PathAndQuery}".StartsWith(failing, StringComparison.Ordinal)
                ? new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("denied") }
                : Respond(HttpStatusCode.OK));
        using var client = new HttpClient(handler, disposeHandler: false);
        var bucket = Bucket(client, string.Empty);

        var exception = await Assert.ThrowsAsync<LLMProviderException>(
            () => upload ? bucket.PutImage(Image, "image/png", cancellationToken) : bucket.Prepare(cancellationToken));

        _ = await Assert.That(exception?.Message ?? string.Empty).StartsWith($"image_upload: {failing}");
        _ = await Assert.That(exception?.Message ?? string.Empty).EndsWith($"returned HTTP {status}: denied");
    }

    [Test]
    [Arguments("", "image_upload.access_key_env is not configured")]
    [Arguments("PARROT_TEST_S3_UNSET", "image_upload: environment variable PARROT_TEST_S3_UNSET is not set")]
    public async Task Missing_credentials_fail_before_any_request(string accessKeyEnv, string message, CancellationToken cancellationToken)
    {
        using var handler = new RecordingHttpHandler(static _ => Respond(HttpStatusCode.OK));
        using var client = new HttpClient(handler, disposeHandler: false);
        var bucket = new S3ImageBucket(client, Config(string.Empty) with { AccessKeyEnv = accessKeyEnv }, new ImmediateTimeProvider(), new InMemoryCredentialStore());

        var exception = await Assert.ThrowsAsync<LLMProviderException>(() => bucket.PutImage(Image, "image/png", cancellationToken));

        _ = await Assert.That(exception?.Message).IsEqualTo(message);
        _ = await Assert.That(handler.Requests).IsEmpty();
    }

    [Test]
    [Arguments(200, true)]
    [Arguments(403, false)]
    [Arguments(404, false)]
    [Arguments(0, false)]
    public async Task Verify_public_is_an_unsigned_head(int status, bool expected, CancellationToken cancellationToken)
    {
        using var handler = new RecordingHttpHandler(_ =>
            status == 0 ? throw new HttpRequestException("unreachable") : Respond((HttpStatusCode)status));
        using var client = new HttpClient(handler, disposeHandler: false);

        var reachable = await Bucket(client, string.Empty).VerifyPublic("https://cdn.example.com/b/parrot/x.png", cancellationToken);

        var request = handler.Requests.Single();
        _ = await Assert.That(reachable).IsEqualTo(expected);
        _ = await Assert.That($"{request.Method} {request.Uri}").IsEqualTo("HEAD https://cdn.example.com/b/parrot/x.png");
        _ = await Assert.That(request.Headers.Keys).IsEmpty();
    }

    [Test]
    public async Task Stored_s3_credentials_override_environment_variables(CancellationToken cancellationToken)
    {
        using var handler = new RecordingHttpHandler(static _ => Respond(HttpStatusCode.OK));
        using var client = new HttpClient(handler, disposeHandler: false);
        var store = new InMemoryCredentialStore();
        await store.Set(S3Credential.ImageUploadName, Credential.ForS3("stored-access", "stored-secret"), cancellationToken);
        var config = Config(string.Empty);
        var bucket = new S3ImageBucket(client, config, new ImmediateTimeProvider(), store);

        _ = await bucket.PutImage(Image, "image/png", cancellationToken);

        var expectedSigner = new AwsV4Signer("stored-access", "stored-secret", config.Region);
        using var expected = new HttpRequestMessage(HttpMethod.Put, handler.Requests.Single().Uri);
        expectedSigner.Sign(expected, ImageHash, DateTimeOffset.UnixEpoch);
        _ = await Assert.That(handler.Requests.Single().Header("Authorization"))
            .IsEqualTo(expected.Headers.GetValues("Authorization").Single());
    }

    [Test]
    public async Task Wrong_stored_credential_type_is_a_permanent_error_without_environment_fallback(CancellationToken cancellationToken)
    {
        using var handler = new RecordingHttpHandler(static _ => Respond(HttpStatusCode.OK));
        using var client = new HttpClient(handler, disposeHandler: false);
        var store = new InMemoryCredentialStore();
        await store.Set(S3Credential.ImageUploadName, Credential.ForApiKey("not-s3"), cancellationToken);
        var bucket = new S3ImageBucket(client, Config(string.Empty), new ImmediateTimeProvider(), store);

        var exception = await Assert.ThrowsAsync<LLMProviderException>(() => bucket.PutImage(Image, "image/png", cancellationToken));

        _ = await Assert.That(exception?.Message).IsEqualTo("image_upload: stored credential must be an s3 credential");
        _ = await Assert.That(handler.Requests).IsEmpty();
    }

    [Test]
    public async Task Concurrent_uploads_share_an_async_credential_read_without_sharing_caller_cancellation(CancellationToken cancellationToken)
    {
        using var handler = new RecordingHttpHandler(static _ => Respond(HttpStatusCode.OK));
        using var client = new HttpClient(handler, disposeHandler: false);
        var store = new DelayedCredentials();
        var bucket = new S3ImageBucket(client, Config(string.Empty), new ImmediateTimeProvider(), store);
        using var canceled = new CancellationTokenSource();
        var abandoned = bucket.PutImage(Image, "image/png", canceled.Token);
        var upload = bucket.PutImage(Image, "image/png", cancellationToken);
        await canceled.CancelAsync();
        _ = await Assert.ThrowsAsync<OperationCanceledException>(() => abandoned.WaitAsync(cancellationToken));
        store.Complete();

        _ = await upload;

        _ = await Assert.That(store.Reads).IsEqualTo(1);
        _ = await Assert.That(handler.Requests.Count).IsEqualTo(1);
    }

    private static S3ImageBucket Bucket(HttpClient client, string publicBaseUrl) =>
        new(client, Config(publicBaseUrl), new ImmediateTimeProvider(), new InMemoryCredentialStore());

    private static ImageUploadConfig Config(string publicBaseUrl) => new()
    {
        Endpoint = "https://minio.example.com:9000",
        Bucket = "b",
        PublicBaseUrl = publicBaseUrl,
        AccessKeyEnv = AccessKeyEnv,
        SecretKeyEnv = SecretKeyEnv,
        ExpiryDays = 3,
    };

    private static HttpResponseMessage Respond(HttpStatusCode status) => new(status) { Content = new StringContent(string.Empty) };

    private static string SetVariable(string name, string value)
    {
        Environment.SetEnvironmentVariable(name, value);
        return name;
    }

    private sealed class DelayedCredentials : ICredentialStore
    {
        private readonly TaskCompletionSource<Credential?> _credential = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Reads { get; private set; }

        public void Complete() => _credential.SetResult(Credential.ForS3("async-access", "async-secret"));

        public async ValueTask<Credential?> Get(string name, CancellationToken cancellationToken)
        {
            Reads++;
            return await _credential.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        public ValueTask Set(string name, Credential credential, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask Delete(string name, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<IReadOnlyList<string>> List(CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
