using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Parrot.Llm.Wire;

namespace Parrot.Llm.ImageUpload;

// Replaces each prompt image with its public bucket URL before the inner
// provider encodes the request. A request the provider rejects before any
// visible output is repaired once: missing objects are re-uploaded, or, when
// every object is reachable, the provider is switched back to inline bytes for
// the rest of the process.
internal sealed class ImageLinkingProvider(ILLMProvider inner, S3ImageBucket bucket) : ILLMProvider
{
    private readonly Dictionary<string, Task<string>> _links = new(StringComparer.Ordinal);
    private volatile bool _inlineBytes;

    public string Id => inner.Id;

    public IUsageReporter? UsageReporter => inner.UsageReporter;

    public long CalculateImageTokens(LLMModel model, LLMContent image) => inner.CalculateImageTokens(model, image);

    public Task<ImageGenerationResult> GenerateImage(ImageGenerationRequest request, CancellationToken cancellationToken) =>
        inner.GenerateImage(request, cancellationToken);

    public IReadOnlyList<LLMModel> SeedModels() => inner.SeedModels();

    public ValueTask<bool> HasCredential(CancellationToken cancellationToken) => inner.HasCredential(cancellationToken);

    public Task<IReadOnlyList<LLMModel>> ListModels(CancellationToken cancellationToken) => inner.ListModels(cancellationToken);

    public ILLMProviderSession OpenSession() => new LinkingSession(inner.OpenSession(), this);

    public IAsyncEnumerable<LLMEvent> Call(LLMRequest request, CancellationToken cancellationToken) =>
        Link(inner.Call, request, cancellationToken);

    private static bool IsVisible(LLMEvent published) =>
        published.Kind is LLMEventKind.TextDelta or LLMEventKind.ReasoningDelta
            or LLMEventKind.ToolCallDelta or LLMEventKind.Completed;

    private static bool IsRejection(Exception failure) => failure is ProviderHttpException or ProviderResponseException;

    // A client error that is neither a limit nor an overload is the provider refusing the request itself.
    private static bool IsUrlRejection(Exception failure) =>
        !ProviderErrors.IsUsageLimit(failure)
        && !ProviderErrors.IsContextLengthExceeded(failure)
        && !ProviderErrors.IsEngineOverloaded(failure)
        && failure is ProviderResponseException
            or ProviderHttpException { StatusCode: >= 400 and < 500 and not (401 or 403 or 408 or 429) };

    private static bool NeedsLink(LLMContent content) => content.Kind == LLMContentKind.Image && content.ImageUrl.Length == 0;

    private async IAsyncEnumerable<LLMEvent> Link(
        Func<LLMRequest, CancellationToken, IAsyncEnumerable<LLMEvent>> call,
        LLMRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var linked = await Rewrite(request, cancellationToken).ConfigureAwait(false);
        var repaired = false;
        while (true)
        {
            Exception? failure = null;
            var visible = false;
            var enumerator = call(linked, cancellationToken).GetAsyncEnumerator(cancellationToken);
            try
            {
                while (true)
                {
                    bool moved;
                    try
                    {
                        moved = await enumerator.MoveNextAsync().ConfigureAwait(false);
                    }
                    catch (Exception caught) when (
                        !repaired && !visible && !ReferenceEquals(linked, request)
                        && !cancellationToken.IsCancellationRequested && IsRejection(caught))
                    {
                        failure = caught;
                        break;
                    }

                    if (!moved)
                    {
                        yield break;
                    }

                    visible |= IsVisible(enumerator.Current);
                    yield return enumerator.Current;
                }
            }
            finally
            {
                await enumerator.DisposeAsync().ConfigureAwait(false);
            }

            if (failure is not { } rejection)
            {
                yield break;
            }

            repaired = true;
            string reason;
            if (await ReuploadMissing(linked, cancellationToken).ConfigureAwait(false))
            {
                linked = await Rewrite(request, cancellationToken).ConfigureAwait(false);
                reason = "Re-uploaded missing image objects. Retrying.";
            }
            else if (IsUrlRejection(rejection))
            {
                _inlineBytes = true;
                linked = request;
                reason = "Provider rejected image URLs. Retrying with inline image data.";
            }
            else
            {
                ExceptionDispatchInfo.Throw(rejection);
                yield break;
            }

            yield return LLMEvent.Retry(1, TimeSpan.Zero, reason);
        }
    }

    private async Task<bool> ReuploadMissing(LLMRequest linked, CancellationToken cancellationToken)
    {
        var missing = new List<string>();
        foreach (var content in linked.Messages.SelectMany(static message => message.Contents)
                     .Where(static content => content.ImageUrl.Length > 0)
                     .DistinctBy(static content => content.ImagePath, StringComparer.Ordinal))
        {
            if (!await bucket.VerifyPublic(content.ImageUrl, cancellationToken).ConfigureAwait(false))
            {
                missing.Add(content.ImagePath);
            }
        }

        if (missing.Count == 0)
        {
            return false;
        }

        bucket.ForgetPreparation();
        foreach (var path in missing)
        {
            Forget(path);
        }

        return true;
    }

    private async Task<LLMRequest> Rewrite(LLMRequest request, CancellationToken cancellationToken)
    {
        if (_inlineBytes || !request.Messages.Any(static message => message.Contents.Any(NeedsLink)))
        {
            return request;
        }

        var messages = new List<LLMMessage>(request.Messages.Count);
        foreach (var message in request.Messages)
        {
            if (!message.Contents.Any(NeedsLink))
            {
                messages.Add(message);
                continue;
            }

            var uploads = message.Contents.Where(NeedsLink)
                .DistinctBy(static content => content.ImagePath, StringComparer.Ordinal)
                .ToDictionary(static content => content.ImagePath, content => Upload(content.ImagePath, content.MediaType, cancellationToken), StringComparer.Ordinal);
            var contents = new List<LLMContent>(message.Contents.Count);
            foreach (var content in message.Contents)
            {
                if (!NeedsLink(content))
                {
                    contents.Add(content);
                    continue;
                }

                var upload = uploads[content.ImagePath];
                contents.Add(LLMContent.ImageLink(
                    content.ImagePath,
                    await upload.ConfigureAwait(false),
                    content.MediaType,
                    content.ImageWidth,
                    content.ImageHeight));
            }

            messages.Add(message with { Contents = contents });
        }

        return request with { Messages = messages };
    }

    // The upload runs detached from any one caller so concurrent callers share one attempt per image;
    // a failed attempt is forgotten so the next call tries again.
    private async Task<string> Upload(string path, string mediaType, CancellationToken cancellationToken)
    {
        Task<string> upload;
        lock (_links)
        {
            if (!_links.TryGetValue(path, out var started))
            {
                started = UploadFile(path, mediaType);
                _links[path] = started;
            }

            upload = started;
        }

        try
        {
            return await upload.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            Forget(path);
            throw;
        }
    }

    private void Forget(string path)
    {
        lock (_links)
        {
            _ = _links.Remove(path);
        }
    }

    private async Task<string> UploadFile(string path, string mediaType)
    {
        await bucket.Prepare(CancellationToken.None).ConfigureAwait(false);
        var bytes = await File.ReadAllBytesAsync(path, CancellationToken.None).ConfigureAwait(false);
        return await bucket.PutImage(bytes, mediaType, CancellationToken.None).ConfigureAwait(false);
    }

    private sealed class LinkingSession(ILLMProviderSession innerSession, ImageLinkingProvider provider) : ILLMProviderSession
    {
        public void BeginTurn() => innerSession.BeginTurn();

        public IAsyncEnumerable<LLMEvent> Call(LLMRequest request, CancellationToken cancellationToken) =>
            provider.Link(innerSession.Call, request, cancellationToken);

        public ValueTask<bool> TryFallBackToHttp() => innerSession.TryFallBackToHttp();

        public ValueTask DisposeAsync() => innerSession.DisposeAsync();
    }
}
