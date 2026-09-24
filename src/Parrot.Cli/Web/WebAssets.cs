using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;

namespace Parrot.Cli.Web;

// Serves the browser UI embedded under the "web/" logical-name prefix. A path
// that names no embedded file is a client-side route, so it gets index.html.
internal sealed class WebAssets(Assembly assembly)
{
    private const string Prefix = "web/";
    private const string Index = "index.html";

    private readonly FileExtensionContentTypeProvider _contentTypes = new();

    public async Task Serve(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var path = context.Request.Path.Value?.TrimStart('/') ?? string.Empty;
        var stream = assembly.GetManifestResourceStream(Prefix + path);
        if (stream is null)
        {
            path = Index;
            stream = assembly.GetManifestResourceStream(Prefix + Index)
                ?? throw new InvalidOperationException("the web UI was not embedded in this build");
        }

        await using (stream.ConfigureAwait(false))
        {
            context.Response.ContentType = _contentTypes.TryGetContentType(path, out var contentType)
                ? contentType
                : "application/octet-stream";
            await stream.CopyToAsync(context.Response.Body, context.RequestAborted).ConfigureAwait(false);
        }
    }
}
