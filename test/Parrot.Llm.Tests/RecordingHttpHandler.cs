namespace Parrot.Core.Tests;

// Records every request (with its body read eagerly) and answers through the supplied function.
internal sealed class RecordingHttpHandler(Func<RecordedRequest, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<RecordedRequest> Requests { get; } = [];

    public int Count(string method, string pathAndQuery) =>
        Requests.Count(request => request.Method == method && request.Uri.PathAndQuery == pathAndQuery);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var header in request.Headers.Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>()))
        {
            headers[header.Key.ToLowerInvariant()] = string.Join(",", header.Value);
        }

        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        var recorded = new RecordedRequest(
            request.Method.Method,
            request.RequestUri ?? throw new InvalidOperationException("The request has no URI."),
            headers,
            body);
        lock (Requests)
        {
            Requests.Add(recorded);
        }

        return respond(recorded);
    }
}
