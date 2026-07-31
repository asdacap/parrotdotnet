using Google.Protobuf;
using Grpc.Core;
using Parrot.Agent;
using Parrot.Protocol;
using Parrot.Security;
using Parrot.Tools;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using GeneratedParrot = Parrot.Protocol.Parrot;

namespace Parrot.Cli;

internal sealed class PromptAttachmentUploader(ToolWorkspace workspace, ModeRegistry modes)
{
    private const int ChunkBytes = 1024 * 1024;

    public async Task<SendMessageRequest?> Prepare(
        GeneratedParrot.ParrotClient client,
        string userSessionId,
        string mode,
        string prompt,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(userSessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(mode);
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(error);

        var request = new SendMessageRequest
        {
            UserSessionId = userSessionId,
            Delivery = Delivery.Steer,
        };
        var security = modes.Resolve(mode).SecurityProfile;

        foreach (var intent in PromptAttachmentParser.Parse(prompt))
        {
            if (intent.Kind == PromptAttachmentIntentKind.Text)
            {
                request.Parts.Add(new MessageContentPart { Text = intent.Value });
                continue;
            }

            ArtifactReference? artifact;
            try
            {
                artifact = await Upload(client, userSessionId, intent.Value, security, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or InvalidOperationException or RpcException)
            {
                await error.WriteLineAsync($"parrot: cannot attach '{intent.Value}': {failure.Message}".AsMemory(), cancellationToken)
                    .ConfigureAwait(false);
                return null;
            }

            request.Parts.Add(new MessageContentPart { ArtifactId = artifact.ArtifactId });
        }

        return request;
    }

    private static string MediaType(IImageFormat format) => format.Name switch
    {
        "PNG" => "image/png",
        "JPEG" => "image/jpeg",
        "GIF" => "image/gif",
        "WEBP" => "image/webp",
        _ => throw new InvalidDataException("Only PNG, JPEG, GIF, and WebP images are supported."),
    };

    private async Task<ArtifactReference> Upload(
        GeneratedParrot.ParrotClient client,
        string userSessionId,
        string path,
        SecurityProfile security,
        CancellationToken cancellationToken)
    {
        var (lexical, physical) = workspace.ResolveRead(path);
        if (!security.AllowsRead(lexical) || !security.AllowsRead(physical))
        {
            throw new UnauthorizedAccessException($"Read access denied for '{path}'.");
        }

        await using var source = File.OpenRead(physical);
        var format = await Image.DetectFormatAsync(source, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The image format is not supported.");
        var mediaType = MediaType(format);
        source.Position = 0;
        using var call = client.UploadAttachment(cancellationToken: cancellationToken);
        await call.RequestStream.WriteAsync(
            new AttachmentUploadFrame
            {
                Header = new AttachmentUploadHeader
                {
                    UserSessionId = userSessionId,
                    UploadId = Guid.CreateVersion7().ToString("n", System.Globalization.CultureInfo.InvariantCulture),
                },
            },
            cancellationToken).ConfigureAwait(false);

        var buffer = GC.AllocateUninitializedArray<byte>(ChunkBytes);
        long byteLength = 0;
        int read;
        while ((read = await source.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            byteLength += read;
            await call.RequestStream.WriteAsync(
                new AttachmentUploadFrame
                {
                    Chunk = ByteString.CopyFrom(buffer, 0, read),
                },
                cancellationToken).ConfigureAwait(false);
        }

        await call.RequestStream.WriteAsync(
            new AttachmentUploadFrame
            {
                Description = new AttachmentUploadDescription
                {
                    DisplayName = Path.GetFileName(path),
                    MediaType = mediaType,
                    ByteLength = byteLength,
                },
            },
            cancellationToken).ConfigureAwait(false);
        await call.RequestStream.CompleteAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        return (await call.ResponseAsync.ConfigureAwait(false)).Artifact;
    }
}
