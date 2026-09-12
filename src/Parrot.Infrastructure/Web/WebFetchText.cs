using System.Net;
using System.Text;

namespace Parrot.Web;

internal static class WebFetchText
{
    private static readonly HashSet<string> AcceptedContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "text/plain", "text/html", "application/json", "text/json", "text/markdown", "text/x-markdown",
        "application/markdown",
    };

    public static string ContentType(HttpResponseMessage response)
    {
        var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;

        if (!AcceptedContentTypes.Contains(contentType))
        {
            throw new WebFetchException("web fetch response used an unsupported content type");
        }

        return contentType.ToLowerInvariant();
    }

    public static Uri Redirect(Uri current, Uri location)
    {
        var target = location.IsAbsoluteUri ? location : new Uri(current, location);

        if (target.Scheme is not ("http" or "https") || target.UserInfo.Length != 0 || target.Host.Length == 0)
        {
            throw new WebFetchException("web fetch redirect used a forbidden address");
        }

        return new UriBuilder(target) { Fragment = string.Empty }.Uri;
    }

    public static string StripControls(string text)
    {
        var output = new StringBuilder(text.Length);

        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.Value is '\n' or '\r' or '\t' || (!Rune.IsControl(rune) && rune != Rune.ReplacementChar))
            {
                _ = output.Append(rune);
            }
        }

        return output.ToString();
    }

    public static bool IsRedirect(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.MovedPermanently
            or HttpStatusCode.Redirect
            or HttpStatusCode.RedirectMethod
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;

    public static string CanonicalHost(string host) => host.TrimEnd('.').ToLowerInvariant();
}
