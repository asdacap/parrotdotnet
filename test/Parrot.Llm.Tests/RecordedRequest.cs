namespace Parrot.Core.Tests;

internal sealed record RecordedRequest(string Method, Uri Uri, IReadOnlyDictionary<string, string> Headers, string Body)
{
    public string Header(string name) => Headers.GetValueOrDefault(name.ToLowerInvariant(), string.Empty);
}
