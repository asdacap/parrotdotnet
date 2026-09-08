using System.Net.Http.Headers;
using System.Text.Json;

namespace Parrot.Llm.Wire;

internal sealed class ImageGenerationClient(
    HttpClient client,
    Uri generationEndpoint,
    Uri editEndpoint,
    Func<CancellationToken, Task<IReadOnlyDictionary<string, string>>> resolveHeaders)
{
    public async Task<ImageGenerationResult> GenerateImage(
        ImageGenerationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        operation.CancelAfter(ImageGenerationLimits.OperationTimeout);
        var token = operation.Token;
        token.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(request.Prompt)
            || request.References.Count > ImageGenerationLimits.MaximumReferences)
        {
            throw new LLMProviderException("provider: image generation requires a prompt and at most five references");
        }

        if (request.Prompt.Length > ImageGenerationLimits.MaximumRequestBytes)
        {
            throw new LLMProviderException("provider: image request exceeds byte limit");
        }

        var references = new List<ImageGenerationWireReference>();
        long totalBytes = 0;
        foreach (var reference in request.References)
        {
            totalBytes += reference.Data.Length;
            if (reference.Data.Length > ImageGenerationLimits.MaximumReferenceBytes
                || totalBytes > ImageGenerationLimits.MaximumReferenceBatchBytes)
            {
                throw new LLMProviderException("provider: image reference exceeds byte limit");
            }

            var mediaType = await ImageGenerationImage.Validate(reference.Data, false, token).ConfigureAwait(false);
            if (mediaType != reference.MediaType)
            {
                throw new LLMProviderException("provider: image reference media type does not match its content");
            }

            references.Add(new($"data:{mediaType};base64,{Convert.ToBase64String(reference.Data)}"));
        }

        var body = JsonSerializer.SerializeToUtf8Bytes(
            new ImageGenerationWireRequest(request.Prompt, references.Count == 0 ? null : [.. references]),
            ImageGenerationJsonContext.Default.ImageGenerationWireRequest);
        if (body.Length > ImageGenerationLimits.MaximumRequestBytes)
        {
            throw new LLMProviderException("provider: image request exceeds byte limit");
        }

        using var message = new HttpRequestMessage(HttpMethod.Post, references.Count == 0 ? generationEndpoint : editEndpoint)
        {
            Content = new ByteArrayContent(body),
        };
        message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        foreach (var (name, value) in await resolveHeaders(token).ConfigureAwait(false))
        {
            _ = message.Headers.TryAddWithoutValidation(name, value);
        }

        using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            // Provider error bodies can echo input images or credentials.
            throw new LLMProviderException($"provider: image generation failed (HTTP {(int)response.StatusCode})");
        }

        if (response.Content.Headers.ContentLength > ImageGenerationLimits.MaximumResponseBytes)
        {
            throw new LLMProviderException("provider: image response exceeds byte limit");
        }

        await using var stream = new BoundedStream(
            await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false),
            ImageGenerationLimits.MaximumResponseBytes,
            response);
        ImageGenerationWireResponse? result;
        try
        {
            result = await JsonSerializer.DeserializeAsync(
                stream, ImageGenerationJsonContext.Default.ImageGenerationWireResponse, token).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            throw new LLMProviderException("provider: invalid image response JSON");
        }

        if (result?.Data is not { Length: 1 } images || images[0]?.B64Json is not { Length: > 0 } encoded)
        {
            throw new LLMProviderException("provider: image response must contain exactly one base64 image");
        }

        if (encoded.Length > ((ImageGenerationLimits.MaximumOutputBytes + 2L) / 3 * 4))
        {
            throw new LLMProviderException("provider: generated image exceeds byte limit");
        }

        byte[] data;
        try
        {
            data = Convert.FromBase64String(encoded);
        }
        catch (FormatException)
        {
            throw new LLMProviderException("provider: invalid image response base64");
        }

        if (data.Length > ImageGenerationLimits.MaximumOutputBytes)
        {
            throw new LLMProviderException("provider: generated image exceeds byte limit");
        }

        _ = await ImageGenerationImage.Validate(data, true, token).ConfigureAwait(false);
        return new(data);
    }
}
