using System.Runtime.CompilerServices;
using System.Text.Json;
using Parrot.Llm.Wire;

namespace Parrot.Llm;

internal sealed class OpenAICompatibleProviderSession(
    Func<LLMRequest, LLMRequest> prepare,
    Func<LLMRequest, CancellationToken, IAsyncEnumerable<LLMEvent>> callHttp,
    Func<CancellationToken, Task<IReadOnlyDictionary<string, string>>> authHeaders,
    ResponsesWebSocketClient websocketClient) : ILLMProviderSession, IProviderSessionFallback
{
    private readonly SemaphoreSlim _exclusive = new(1, 1);
    private ResponsesWebSocket? _connection;
    private CompletedResponse? _completedResponse;
    private bool _httpOnly;
    private bool _disposed;

    private enum Recovery
    {
        None,
        Full,
        Http,
    }

    public async IAsyncEnumerable<LLMEvent> Call(
        LLMRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _exclusive.WaitAsync(cancellationToken).ConfigureAwait(false);
        WebSocketAttempt? attempt = null;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_httpOnly)
            {
                await foreach (var published in callHttp(request, cancellationToken).ConfigureAwait(false))
                {
                    yield return published;
                }

                yield break;
            }

            var prepared = ResponsesAdapter.Prepare(prepare(request));
            var previousResponse = _completedResponse;
            _completedResponse = null;
            attempt = Begin(prepared, IncrementalRequest.Select(prepared, previousResponse));
            var recovered = false;
            while (true)
            {
                var step = await Step(attempt, !recovered, cancellationToken).ConfigureAwait(false);
                if (step.Event is { } published)
                {
                    yield return published;
                    continue;
                }

                if (step.Recovery == Recovery.None)
                {
                    yield break;
                }

                await attempt.DisposeAsync().ConfigureAwait(false);
                attempt = null;
                if (step.Recovery == Recovery.Http)
                {
                    await SelectHttp().ConfigureAwait(false);
                    await foreach (var httpEvent in callHttp(request, cancellationToken).ConfigureAwait(false))
                    {
                        yield return httpEvent;
                    }

                    yield break;
                }

                await Poison().ConfigureAwait(false);
                _completedResponse = null;
                recovered = true;
                attempt = Begin(prepared, IncrementalRequest.Full(prepared));
            }
        }
        finally
        {
            try
            {
                if (attempt is not null)
                {
                    await attempt.DisposeAsync().ConfigureAwait(false);
                    if (!attempt.Response.Done)
                    {
                        _completedResponse = null;
                        await Poison().ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                _ = _exclusive.Release();
            }
        }
    }

    public async ValueTask FallBackToHttp()
    {
        _ = await _exclusive.WaitAsync(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await SelectHttp().ConfigureAwait(false);
        }
        finally
        {
            _ = _exclusive.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _ = await _exclusive.WaitAsync(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            await Poison().ConfigureAwait(false);
        }
        finally
        {
            _ = _exclusive.Release();
            _exclusive.Dispose();
        }
    }

    private static bool Unsupported(int statusCode) => statusCode is 404 or 405 or 426;

    private static bool IsVisible(LLMEvent published) =>
        published.Kind is LLMEventKind.TextDelta or LLMEventKind.ReasoningDelta
            or LLMEventKind.ToolCallDelta or LLMEventKind.Completed;

    private WebSocketAttempt Begin(
        ResponsesAdapter.PreparedRequest prepared,
        IncrementalRequest request) =>
        new(prepared, request, GetConnection);

    private async Task<AttemptStep> Step(
        WebSocketAttempt attempt,
        bool mayRecover,
        CancellationToken cancellationToken)
    {
        try
        {
            if (await attempt.MoveNext(cancellationToken).ConfigureAwait(false))
            {
                var published = attempt.Current;
                attempt.Visible |= IsVisible(published);
                if (published.Kind == LLMEventKind.Completed)
                {
                    StoreCompletion(attempt.Prepared, attempt.Response);
                }

                return AttemptStep.Emitting(published);
            }

            if (_completedResponse is null)
            {
                await Poison().ConfigureAwait(false);
            }

            await attempt.DisposeAsync().ConfigureAwait(false);
            return AttemptStep.Done;
        }
        catch (ProviderResponseException failure) when (mayRecover && !attempt.Visible
            && failure.ErrorCode is "previous_response_not_found" or "websocket_connection_limit_reached")
        {
            return AttemptStep.Recovering(Recovery.Full);
        }
        catch (ResponsesWebSocketUpgradeException failure) when (Unsupported(failure.StatusCode))
        {
            return AttemptStep.Recovering(Recovery.Http);
        }
        catch (OperationCanceledException)
        {
            await Poison().ConfigureAwait(false);
            _completedResponse = null;
            throw;
        }
        catch (Exception failure)
        {
            await Poison().ConfigureAwait(false);
            _completedResponse = null;
            if (attempt.Visible)
            {
                _httpOnly = true;
            }

            if (failure is ProviderResponseException or LLMProviderException
                or ResponsesWebSocketTransportException or ResponsesWebSocketUpgradeException)
            {
                throw;
            }

            throw new ResponsesWebSocketTransportException("Responses WebSocket transport failed.", failure);
        }
    }

    private void StoreCompletion(
        ResponsesAdapter.PreparedRequest prepared,
        ResponsesAdapter.ParseState response) =>
        _completedResponse = response.FinishReason is "stop" or "tool_calls" && response.ResponseId.Length > 0
            ? new CompletedResponse(prepared, response.ResponseId, [.. response.Output])
            : null;

    private async Task<ResponsesWebSocket> GetConnection(CancellationToken cancellationToken)
    {
        if (_connection is null || _connection.State != System.Net.WebSockets.WebSocketState.Open)
        {
            await Poison().ConfigureAwait(false);
            _connection = await websocketClient.Connect(
                await authHeaders(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        }

        return _connection;
    }

    private async ValueTask Poison()
    {
        if (_connection is null)
        {
            return;
        }

        await _connection.DisposeAsync().ConfigureAwait(false);
        _connection = null;
    }

    private async ValueTask SelectHttp()
    {
        _httpOnly = true;
        _completedResponse = null;
        await Poison().ConfigureAwait(false);
    }

    private sealed class WebSocketAttempt(
        ResponsesAdapter.PreparedRequest prepared,
        IncrementalRequest request,
        Func<CancellationToken, Task<ResponsesWebSocket>> getConnection) : IAsyncDisposable
    {
        private IAsyncEnumerator<LLMEvent>? _enumerator;

        public ResponsesAdapter.PreparedRequest Prepared { get; } = prepared;

        public ResponsesAdapter.ParseState Response { get; } = new();

        public bool Visible { get; set; }

        public LLMEvent Current => _enumerator?.Current
            ?? throw new InvalidOperationException("No WebSocket response event is current.");

        public async ValueTask<bool> MoveNext(CancellationToken cancellationToken)
        {
            if (_enumerator is null)
            {
                var connection = await getConnection(cancellationToken).ConfigureAwait(false);
                _enumerator = connection.Send(
                    Prepared.EncodeWebSocket(request.PreviousResponseId, request.Input),
                    Response,
                    cancellationToken).GetAsyncEnumerator(cancellationToken);
            }

            return await _enumerator.MoveNextAsync().ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            if (_enumerator is not null)
            {
                await _enumerator.DisposeAsync().ConfigureAwait(false);
                _enumerator = null;
            }
        }
    }

    private sealed record AttemptStep
    {
        public static AttemptStep Done { get; } = new();

        public LLMEvent? Event { get; init; }

        public Recovery Recovery { get; init; }

        public static AttemptStep Emitting(LLMEvent published) => new() { Event = published };

        public static AttemptStep Recovering(Recovery recovery) => new() { Recovery = recovery };
    }

    private sealed record CompletedResponse(
        ResponsesAdapter.PreparedRequest Request,
        string ResponseId,
        IReadOnlyList<ResponsesAdapter.InputItem> Output);

    private sealed record IncrementalRequest(
        string PreviousResponseId,
        IReadOnlyList<ResponsesAdapter.InputItem> Input)
    {
        public static IncrementalRequest Full(ResponsesAdapter.PreparedRequest request) =>
            new(string.Empty, request.Input);

        public static IncrementalRequest Select(
            ResponsesAdapter.PreparedRequest current,
            CompletedResponse? previous)
        {
            if (previous is null || !SameProperties(current, previous.Request))
            {
                return Full(current);
            }

            var prefixLength = previous.Request.Input.Count + previous.Output.Count;
            if (current.Input.Count <= prefixLength
                || !EqualItems(current.Input, 0, previous.Request.Input)
                || !EqualItems(current.Input, previous.Request.Input.Count, previous.Output))
            {
                return Full(current);
            }

            return new IncrementalRequest(previous.ResponseId, [.. current.Input.Skip(prefixLength)]);
        }

        private static bool SameProperties(
            ResponsesAdapter.PreparedRequest current,
            ResponsesAdapter.PreparedRequest previous)
        {
            var currentBody = current.Body;
            var previousBody = previous.Body;
            return currentBody.Model == previousBody.Model
                && currentBody.Instructions == previousBody.Instructions
                && currentBody.Stream == previousBody.Stream
                && currentBody.Store == previousBody.Store
                && currentBody.MaxOutputTokens == previousBody.MaxOutputTokens
                && JsonEqual(currentBody.Tools, previousBody.Tools, WireJsonContext.Default.ResponsesFunctionTools)
                && JsonEqual(currentBody.Reasoning, previousBody.Reasoning, WireJsonContext.Default.ResponsesReasoning)
                && NullableJsonEqual(currentBody.Provider, previousBody.Provider);
        }

        private static bool EqualItems(
            IReadOnlyList<ResponsesAdapter.InputItem> candidate,
            int offset,
            IReadOnlyList<ResponsesAdapter.InputItem> expected)
        {
            if (candidate.Count < offset + expected.Count)
            {
                return false;
            }

            for (var index = 0; index < expected.Count; index++)
            {
                if (!JsonEqual(candidate[offset + index], expected[index], WireJsonContext.Default.ResponsesInputItem))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool NullableJsonEqual(JsonElement? left, JsonElement? right) =>
            left.HasValue == right.HasValue
            && (!left.HasValue || JsonElement.DeepEquals(left.GetValueOrDefault(), right.GetValueOrDefault()));

        private static bool JsonEqual<T>(
            T left,
            T right,
            System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo) =>
            JsonSerializer.Serialize(left, typeInfo) == JsonSerializer.Serialize(right, typeInfo);
    }
}
