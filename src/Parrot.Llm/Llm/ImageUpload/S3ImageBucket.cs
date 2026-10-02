using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Parrot.Config;

namespace Parrot.Llm.ImageUpload;

// One S3-compatible bucket holding prompt images under content-addressed keys.
// Preparation (creation, expiry lifecycle, public-read policy) runs once per
// process and is repeated only after ForgetPreparation. Every failure is
// permanent: the bucket is assumed to be configured correctly.
internal sealed class S3ImageBucket(HttpClient client, ImageUploadConfig config, TimeProvider timeProvider)
{
    private static readonly TimeSpan ControlTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan UploadTimeout = TimeSpan.FromMinutes(2);
    private readonly Lock _lock = new();
    private readonly Uri _endpoint = new(config.Endpoint.TrimEnd('/') + "/");
    private readonly string _publicBase = (config.PublicBaseUrl.Length > 0 ? config.PublicBaseUrl : config.Endpoint).TrimEnd('/');
    private AwsV4Signer? _signer;
    private Task? _prepared;

    // Creates the bucket when missing, then installs the expiry lifecycle rule and the public-read policy for the key prefix.
    public Task Prepare(CancellationToken cancellationToken)
    {
        Task preparation;
        lock (_lock)
        {
            if (_prepared is null || _prepared.IsFaulted || _prepared.IsCanceled)
            {
                _prepared = PrepareBucket();
            }

            preparation = _prepared;
        }

        return preparation.WaitAsync(cancellationToken);
    }

    // Makes the next Prepare run the bucket setup again.
    public void ForgetPreparation()
    {
        lock (_lock)
        {
            _prepared = null;
        }
    }

    // Uploads the bytes under their SHA-256 key and returns the public URL.
    public async Task<string> PutImage(byte[] bytes, string mediaType, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var key = $"{config.KeyPrefix}{hash}{Extension(mediaType)}";
        using var message = new HttpRequestMessage(HttpMethod.Put, new Uri(_endpoint, $"{config.Bucket}/{key}"))
        {
            Content = new ByteArrayContent(bytes),
        };
        message.Content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        using var response = await Send(message, hash, UploadTimeout, cancellationToken).ConfigureAwait(false);
        await EnsureSuccess(response, message).ConfigureAwait(false);
        return $"{_publicBase}/{config.Bucket}/{key}";
    }

    // Checks, without credentials, that the URL serves an object; false when it is missing or private.
    public async Task<bool> VerifyPublic(string url, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Head, new Uri(url));
        try
        {
            using var response = await Dispatch(message, ControlTimeout, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    private static string Extension(string mediaType) => mediaType switch
    {
        "image/png" => ".png",
        "image/jpeg" => ".jpg",
        "image/gif" => ".gif",
        "image/webp" => ".webp",
        _ => throw new LLMProviderException($"image_upload: unsupported image media type \"{mediaType}\""),
    };

    private static string Variable(string field, string name)
    {
        if (name.Length == 0)
        {
            throw new LLMProviderException($"image_upload.{field} is not configured");
        }

        return Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new LLMProviderException($"image_upload: environment variable {name} is not set");
    }

    private static string PayloadHash(string body) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body)));

    private static async Task EnsureSuccess(HttpResponseMessage response, HttpRequestMessage message)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = ProviderErrors.BoundResponseBody(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        throw new LLMProviderException(
            $"image_upload: {message.Method.Method} {message.RequestUri?.PathAndQuery} returned HTTP {(int)response.StatusCode}: {body}");
    }

    private async Task PrepareBucket()
    {
        using var head = new HttpRequestMessage(HttpMethod.Head, new Uri(_endpoint, config.Bucket));
        using var exists = await Send(head, AwsV4Signer.EmptyPayloadSha256, ControlTimeout, CancellationToken.None).ConfigureAwait(false);
        if (exists.StatusCode == HttpStatusCode.NotFound)
        {
            var location = config.Region == "us-east-1"
                ? string.Empty
                : $"<CreateBucketConfiguration><LocationConstraint>{config.Region}</LocationConstraint></CreateBucketConfiguration>";
            using var create = Control(config.Bucket, location, "application/xml");
            using var created = await Send(create, PayloadHash(location), ControlTimeout, CancellationToken.None).ConfigureAwait(false);
            if (created.StatusCode != HttpStatusCode.Conflict)
            {
                await EnsureSuccess(created, create).ConfigureAwait(false);
            }
        }
        else
        {
            await EnsureSuccess(exists, head).ConfigureAwait(false);
        }

        var lifecycle = "<LifecycleConfiguration><Rule><ID>parrot-image-expiry</ID>"
            + $"<Filter><Prefix>{config.KeyPrefix}</Prefix></Filter><Status>Enabled</Status>"
            + $"<Expiration><Days>{config.ExpiryDays}</Days></Expiration></Rule></LifecycleConfiguration>";
        using var putLifecycle = Control($"{config.Bucket}?lifecycle", lifecycle, "application/xml");
        _ = putLifecycle.Headers.TryAddWithoutValidation("x-amz-sdk-checksum-algorithm", "SHA256");
        _ = putLifecycle.Headers.TryAddWithoutValidation(
            "x-amz-checksum-sha256", Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(lifecycle))));
        using var lifecycleResponse = await Send(putLifecycle, PayloadHash(lifecycle), ControlTimeout, CancellationToken.None).ConfigureAwait(false);
        await EnsureSuccess(lifecycleResponse, putLifecycle).ConfigureAwait(false);

        var policy = "{\"Version\":\"2012-10-17\",\"Statement\":[{\"Sid\":\"parrot-public-read\",\"Effect\":\"Allow\","
            + "\"Principal\":{\"AWS\":[\"*\"]},\"Action\":[\"s3:GetObject\"],"
            + $"\"Resource\":[\"arn:aws:s3:::{config.Bucket}/{config.KeyPrefix}*\"]}}]}}";
        using var putPolicy = Control($"{config.Bucket}?policy", policy, "application/json");
        using var policyResponse = await Send(putPolicy, PayloadHash(policy), ControlTimeout, CancellationToken.None).ConfigureAwait(false);
        await EnsureSuccess(policyResponse, putPolicy).ConfigureAwait(false);
    }

    private HttpRequestMessage Control(string relative, string body, string contentType) =>
        new(HttpMethod.Put, new Uri(_endpoint, relative))
        {
            Content = new StringContent(body, Encoding.UTF8, contentType),
        };

    private AwsV4Signer Signer()
    {
        lock (_lock)
        {
            return _signer ??= new AwsV4Signer(
                Variable("access_key_env", config.AccessKeyEnv),
                Variable("secret_key_env", config.SecretKeyEnv),
                config.Region);
        }
    }

    private Task<HttpResponseMessage> Send(
        HttpRequestMessage message, string payloadSha256Hex, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Signer().Sign(message, payloadSha256Hex, timeProvider.GetUtcNow());
        return Dispatch(message, timeout, cancellationToken);
    }

    private async Task<HttpResponseMessage> Dispatch(HttpRequestMessage message, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            return await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException failure) when (!cancellationToken.IsCancellationRequested)
        {
            throw new LLMProviderException($"image_upload: {message.Method.Method} {message.RequestUri?.PathAndQuery} timed out", failure);
        }
    }
}
