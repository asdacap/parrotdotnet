using System.Text.Json;
using System.Text.Json.Serialization;
using Parrot.Agent;
using Parrot.Files;
using Parrot.Llm;

namespace Parrot.Tools;

internal sealed class ImageGenerationTool(ToolWorkspace workspace) : ITool
{
    public string Name => "imagegen";

    public async Task<ToolExecutionResult> Execute(
        ToolInvocation invocation,
        AgentTurnSelection selection,
        CancellationToken cancellationToken)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        operation.CancelAfter(ImageGenerationLimits.OperationTimeout);
        var token = operation.Token;
        try
        {
            token.ThrowIfCancellationRequested();
            var input = JsonSerializer.Deserialize(invocation.ArgumentsJson, FileToolJsonContext.Default.ImageGenerationToolInput)
                ?? throw new FormatException("Tool arguments must be an object.");
            if (string.IsNullOrWhiteSpace(input.Prompt) || string.IsNullOrWhiteSpace(input.OutputPath))
            {
                throw new FormatException("Tool arguments require nonblank 'prompt' and 'output_path' strings.");
            }

            if (!string.Equals(Path.GetExtension(input.OutputPath), ".png", StringComparison.OrdinalIgnoreCase))
            {
                throw new FormatException("Image output_path must have a .png extension.");
            }

            var paths = input.ReferencedImagePaths ?? [];
            if (paths.Length > ImageGenerationLimits.MaximumReferences)
            {
                throw new FormatException("At most five referenced_image_paths are supported.");
            }

            var output = workspace.ResolveMutation(input.OutputPath, create: true, selection.SecurityProfile);
            FileMutation.RequireRegularFileOrMissing(output.Physical);
            var references = new List<ImageGenerationReference>();
            long totalBytes = 0;
            foreach (var path in paths)
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    throw new FormatException("Referenced image paths must be nonblank strings.");
                }

                await using var source = workspace.OpenRegularReadWithoutLinks(path, selection.SecurityProfile);
                using var buffer = new MemoryStream();
                var chunk = new byte[81920];
                int read;
                while ((read = await source.ReadAsync(chunk, token).ConfigureAwait(false)) != 0)
                {
                    totalBytes += read;
                    if (buffer.Length + read > ImageGenerationLimits.MaximumReferenceBytes
                        || totalBytes > ImageGenerationLimits.MaximumReferenceBatchBytes)
                    {
                        throw new FormatException("Image references exceed the supported byte limits.");
                    }

                    await buffer.WriteAsync(chunk.AsMemory(0, read), token).ConfigureAwait(false);
                }

                var data = buffer.ToArray();
                var mediaType = await ImageGenerationImage.Validate(data, false, token).ConfigureAwait(false);
                references.Add(new ImageGenerationReference(mediaType, data));
            }

            var result = await selection.ResolvedModel.CanonicalModel.Provider.GenerateImage(
                new ImageGenerationRequest(input.Prompt, references), token).ConfigureAwait(false);
            if (result.Data.Length > ImageGenerationLimits.MaximumOutputBytes)
            {
                throw new LLMProviderException("Generated image exceeds the supported byte limit.");
            }

            _ = await ImageGenerationImage.Validate(result.Data, true, token).ConfigureAwait(false);
            output = workspace.ResolveMutation(input.OutputPath, create: true, selection.SecurityProfile);
            FileMutation.RequireRegularFileOrMissing(output.Physical);
            await FileMutation.Write(output.Physical, result.Data, createParents: true, token).ConfigureAwait(false);
            return ToolResultFormatter.Text(invocation, output.Physical);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception failure) when (failure is JsonException or FormatException or IOException
            or UnauthorizedAccessException or InvalidOperationException or ArgumentException or LLMProviderException
            or Llm.Wire.WireProtocolException)
        {
            return ToolResultFormatter.Error(invocation, failure.Message);
        }
        catch (Exception failure) when (failure is HttpRequestException or Auth.AuthException)
        {
            return ToolResultFormatter.Error(invocation, "Image generation authentication or network request failed.");
        }
    }

    internal sealed class Input
    {
        [JsonPropertyName("prompt")]
        public string? Prompt { get; init; }

        [JsonPropertyName("output_path")]
        public string? OutputPath { get; init; }

        [JsonPropertyName("referenced_image_paths")]
        public string?[]? ReferencedImagePaths { get; init; }
    }
}
