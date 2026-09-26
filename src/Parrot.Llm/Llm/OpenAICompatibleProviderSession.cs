using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using Parrot.Llm.Wire;

namespace Parrot.Llm;

internal sealed class OpenAICompatibleProviderSession(
    Func<LLMRequest, LLMRequest> prepare,
    Func<LLMRequest, string, Action<string>, CancellationToken, IAsyncEnumerable<LLMEvent>> callHttp,
    Func<CancellationToken, Task<IReadOnlyDictionary<string, string>>> authHeaders,
    bool disableWebSocket,
    ResponsesWebSocketClient websocketClient) : ILLMProviderSession
{
    private readonly SemaphoreSlim _exclusive = new(1, 1);
    private ResponsesWebSocket? _connection;
    private CompletedResponse? _completedResponse;
    private string? _turnState;
    private bool _httpOnly = disableWebSocket;
    private bool _disposed;

    private enum Recovery
    {
        None,
        Full,
        Http,
    }

    public void BeginTurn() => _turnState = null;

    public async IAsyncEnumerable<LLMEvent> Call(
        LLMRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _exclusive.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_httpOnly)
            {
                await foreach (var published in callHttp(request, _turnState ?? string.Empty, CaptureTurnState, cancellationToken).ConfigureAwait(false))
                {
                    yield return published;
                }

                yield break;
            }

            var prepared = ResponsesAdapter.Prepare(prepare(request));
            var settings = Settings(prepared);
            var inputHashes = prepared.Input.Select(Hash).ToList();
            var previousResponse = _completedResponse;
            _completedResponse = null;
            var incrementalRequest = IncrementalRequest.Select(prepared, settings, inputHashes, previousResponse);
            var recovered = false;
            while (true)
            {
                AttemptStep recovery;
                await using (var attempt = new WebSocketAttempt(
                    prepared, settings, inputHashes, incrementalRequest, GetTurnState, GetConnection, CaptureTurnState, request.Diagnostics))
                {
                    var releasedForRecovery = false;
                    try
                    {
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

                            await attempt.Release().ConfigureAwait(false);
                            releasedForRecovery = true;
                            recovery = step;
                            break;
                        }
                    }
                    finally
                    {
                        await attempt.DisposeAsync().ConfigureAwait(false);
                        if (!releasedForRecovery && !attempt.Response.Done)
                        {
                            _completedResponse = null;
                            await Poison().ConfigureAwait(false);
                        }
                    }
                }

                if (recovery.Recovery == Recovery.Http)
                {
                    await SelectHttp().ConfigureAwait(false);
                    yield return LLMEvent.Retry(1, TimeSpan.Zero, recovery.Reason);
                    await foreach (var httpEvent in callHttp(request, _turnState ?? string.Empty, CaptureTurnState, cancellationToken).ConfigureAwait(false))
                    {
                        yield return httpEvent;
                    }

                    yield break;
                }

                await Poison().ConfigureAwait(false);
                _completedResponse = null;
                recovered = true;
                incrementalRequest = IncrementalRequest.Full(prepared);
                yield return LLMEvent.Retry(1, TimeSpan.Zero, recovery.Reason);
            }
        }
        finally
        {
            _ = _exclusive.Release();
        }
    }

    public async ValueTask<bool> TryFallBackToHttp()
    {
        _ = await _exclusive.WaitAsync(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await SelectHttp().ConfigureAwait(false);
            return true;
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

    // Settings and items are kept as hashes so no request body, and no Base64 image, outlives its call.
    private static string Settings(ResponsesAdapter.PreparedRequest prepared) =>
        Convert.ToHexStringLower(SHA256.HashData(prepared.EncodeWebSocket(string.Empty, [], string.Empty)));

    private static string Hash(ResponsesAdapter.InputItem item) =>
        Convert.ToHexStringLower(SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes(item, WireJsonContext.Default.ResponsesInputItem)));

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
                    StoreCompletion(attempt);
                }

                return AttemptStep.Emitting(published);
            }

            if (_completedResponse is null)
            {
                await Poison().ConfigureAwait(false);
            }

            await attempt.Release().ConfigureAwait(false);
            return AttemptStep.Done;
        }
        catch (ProviderResponseException failure) when (mayRecover && !attempt.Visible
            && failure.ErrorCode is "previous_response_not_found" or "websocket_connection_limit_reached")
        {
            var reason = failure.ErrorCode == "previous_response_not_found"
                ? "Previous provider response is unavailable. Retrying the full request on a new WebSocket connection."
                : "WebSocket connection limit reached. Retrying the full request on a new connection.";
            return AttemptStep.Recovering(Recovery.Full, reason);
        }
        catch (ResponsesWebSocketUpgradeException failure) when (Unsupported(failure.StatusCode))
        {
            return AttemptStep.Recovering(Recovery.Http, $"WebSocket upgrade is unsupported (HTTP {failure.StatusCode}). Retrying over HTTP.");
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

    private void StoreCompletion(WebSocketAttempt attempt) =>
        _completedResponse = attempt.Response.FinishReason is "stop" or "tool_calls" && attempt.Response.ResponseId.Length > 0
            ? new CompletedResponse(attempt.Settings, attempt.Response.ResponseId, [.. attempt.InputHashes, .. attempt.Response.Output.Select(Hash)])
            : null;

    private async Task<ResponsesWebSocket> GetConnection(CancellationToken cancellationToken)
    {
        if (_connection is null || _connection.State != System.Net.WebSockets.WebSocketState.Open)
        {
            await Poison().ConfigureAwait(false);
            _connection = await websocketClient.Connect(
                await authHeaders(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
            foreach (var header in _connection.ResponseHeaders)
            {
                if (header.Key.Equals("x-codex-turn-state", StringComparison.OrdinalIgnoreCase))
                {
                    CaptureTurnState(header.Value);
                    break;
                }
            }
        }

        return _connection;
    }

    private string GetTurnState() => _turnState ?? string.Empty;

    private void CaptureTurnState(string turnState)
    {
        if (_turnState is null && turnState.Length > 0)
        {
            _turnState = turnState;
        }
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
        string settings,
        IReadOnlyList<string> inputHashes,
        IncrementalRequest request,
        Func<string> getTurnState,
        Func<CancellationToken, Task<ResponsesWebSocket>> getConnection,
        Action<string> captureTurnState,
        IProviderRequestDiagnostics? diagnostics) : IAsyncDisposable
    {
        private IAsyncEnumerator<LLMEvent>? _enumerator;
        private bool _disposalStarted;

        public ResponsesAdapter.PreparedRequest Prepared { get; } = prepared;

        public string Settings { get; } = settings;

        public IReadOnlyList<string> InputHashes { get; } = inputHashes;

        public ResponsesAdapter.ParseState Response { get; } = new() { CaptureTurnState = captureTurnState };

        public bool Visible { get; set; }

        public LLMEvent Current => _enumerator?.Current
            ?? throw new InvalidOperationException("No WebSocket response event is current.");

        public async ValueTask<bool> MoveNext(CancellationToken cancellationToken)
        {
            if (_enumerator is null)
            {
                var events = Send(null, cancellationToken);
                _enumerator = (diagnostics is null ? events : diagnostics.Trace(Send, "websocket", cancellationToken))
                    .GetAsyncEnumerator(cancellationToken);
            }

            return await _enumerator.MoveNextAsync().ConfigureAwait(false);
        }

        public ValueTask DisposeAsync()
        {
            if (_disposalStarted)
            {
                return ValueTask.CompletedTask;
            }

            // The lexical owner must not retry a failed terminal disposal after explicit cleanup.
            _disposalStarted = true;
            return Release();
        }

        public async ValueTask Release()
        {
            if (_enumerator is not null)
            {
                await _enumerator.DisposeAsync().ConfigureAwait(false);
                _enumerator = null;
            }
        }

        private async IAsyncEnumerable<LLMEvent> Send(IProviderAttemptDiagnostics? attempt, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var connection = await getConnection(cancellationToken).ConfigureAwait(false);
            var body = Prepared.EncodeWebSocket(request.PreviousResponseId, request.Input, getTurnState());
            diagnostics?.DumpRequest(body);
            await foreach (var published in connection.Send(
                body,
                Response,
                attempt,
                cancellationToken).ConfigureAwait(false))
            {
                yield return published;
            }
        }
    }

    private sealed record AttemptStep
    {
        public static AttemptStep Done { get; } = new();

        public LLMEvent? Event { get; init; }

        public Recovery Recovery { get; init; }

        public string Reason { get; init; } = string.Empty;

        public static AttemptStep Emitting(LLMEvent published) => new() { Event = published };

        public static AttemptStep Recovering(Recovery recovery, string reason) => new() { Recovery = recovery, Reason = reason };
    }

    private sealed record CompletedResponse(
        string Settings,
        string ResponseId,
        IReadOnlyList<string> Items);

    private sealed record IncrementalRequest(
        string PreviousResponseId,
        IReadOnlyList<ResponsesAdapter.InputItem> Input)
    {
        public static IncrementalRequest Full(ResponsesAdapter.PreparedRequest request) =>
            new(string.Empty, request.Input);

        public static IncrementalRequest Select(
            ResponsesAdapter.PreparedRequest current,
            string settings,
            List<string> inputHashes,
            CompletedResponse? previous) =>
            previous is null
                || previous.Settings != settings
                || inputHashes.Count <= previous.Items.Count
                || !inputHashes.Take(previous.Items.Count).SequenceEqual(previous.Items)
                ? Full(current)
                : new IncrementalRequest(previous.ResponseId, [.. current.Input.Skip(previous.Items.Count)]);
    }
}
