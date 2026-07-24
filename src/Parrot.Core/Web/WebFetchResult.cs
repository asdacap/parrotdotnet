namespace Parrot.Web;

internal sealed record WebFetchResult(
    Uri FinalAddress,
    int StatusCode,
    string ContentType,
    string Text,
    bool Truncated);
