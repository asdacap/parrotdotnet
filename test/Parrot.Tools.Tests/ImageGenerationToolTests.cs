using System.Diagnostics;
using System.Text.Json;
using Parrot.Agent;
using Parrot.Llm;
using Parrot.Security;
using Parrot.Tools;

namespace Parrot.Core.Tests;

internal sealed class ImageGenerationToolTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parrot-imagegen-tests", Guid.NewGuid().ToString("n"));

    public ImageGenerationToolTests() => _ = Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task Saves_png_and_snapshots_in_place_edits(bool existing, bool edit, CancellationToken cancellationToken)
    {
        var outputPath = Path.Combine(_root, "output.png");
        var original = Png().Concat(new byte[200]).ToArray();
        if (existing)
        {
            await File.WriteAllBytesAsync(outputPath, original, cancellationToken);
        }

        var provider = new ImageProvider("images", async (request, token) =>
        {
            _ = await Assert.That(request.Prompt).IsEqualTo("  Paint a café\nverbatim.  ");
            _ = await Assert.That(request.References.Count).IsEqualTo(edit ? 1 : 0);
            _ = await Assert.That(File.Exists(outputPath)).IsEqualTo(existing);
            if (existing)
            {
                _ = await Assert.That((await File.ReadAllBytesAsync(outputPath, token)).SequenceEqual(original)).IsTrue();
            }

            if (edit)
            {
                _ = await Assert.That(request.References[0].MediaType).IsEqualTo("image/png");
                _ = await Assert.That(request.References[0].Data.SequenceEqual(original)).IsTrue();
            }

            return new ImageGenerationResult(Png());
        });
        ITool tool = new ImageGenerationTool(new ToolWorkspace(_root));
        var result = await tool.Execute(
            new ToolInvocation("generate", EncodeArguments("  Paint a café\nverbatim.  ", "output.png", edit ? ["output.png"] : [])),
            new TurnFixture(provider, WritableProfile()).Selection,
            cancellationToken);

        _ = await Assert.That(result.Text).DoesNotStartWith("error: ");
        _ = await Assert.That(result.Text).Contains("output.png");
        _ = await Assert.That(result.ImageArtifacts).IsEmpty();
        _ = await Assert.That(provider.Calls).IsEqualTo(1);
        _ = await Assert.That((await File.ReadAllBytesAsync(outputPath, cancellationToken)).SequenceEqual(Png())).IsTrue();
    }

    [Test]
    public async Task Uses_each_invocations_current_provider_including_wrappers_and_unsupported(CancellationToken cancellationToken)
    {
        var first = new ImageProvider("first", GeneratePng);
        var second = new ImageProvider("second", GeneratePng);
        ITool tool = new ImageGenerationTool(new ToolWorkspace(_root));
        foreach (var (provider, unsupported) in new (ILLMProvider Provider, bool Unsupported)[]
        {
            (first, false),
            (new RetryingProvider(second), false),
            (new UnusedProvider(), true),
        })
        {
            var result = await tool.Execute(
                new ToolInvocation("generate", EncodeArguments("draw", "output.png", [])),
                new TurnFixture(provider, WritableProfile()).Selection,
                cancellationToken);
            _ = await Assert.That(result.Text.StartsWith("error: ", StringComparison.Ordinal)).IsEqualTo(unsupported);
            _ = await Assert.That(result.ImageArtifacts).IsEmpty();
        }

        _ = await Assert.That(first.Calls).IsEqualTo(1);
        _ = await Assert.That(second.Calls).IsEqualTo(1);
        _ = await Assert.That((await File.ReadAllBytesAsync(Path.Combine(_root, "output.png"), cancellationToken)).SequenceEqual(Png())).IsTrue();
    }

    [Test]
    [Arguments("{")]
    [Arguments("null")]
    [Arguments("[]")]
    [Arguments("{}")]
    [Arguments("{\"prompt\":\"draw\"}")]
    [Arguments("{\"output_path\":\"output.png\"}")]
    [Arguments("{\"prompt\":null,\"output_path\":\"output.png\"}")]
    [Arguments("{\"prompt\":1,\"output_path\":\"output.png\"}")]
    [Arguments("{\"prompt\":\"  \",\"output_path\":\"output.png\"}")]
    [Arguments("{\"prompt\":\"draw\",\"output_path\":false}")]
    [Arguments("{\"prompt\":\"draw\",\"output_path\":null}")]
    [Arguments("{\"prompt\":\"draw\",\"output_path\":\"  \"}")]
    [Arguments("{\"prompt\":\"draw\",\"output_path\":\"output.jpg\"}")]
    [Arguments("{\"prompt\":\"draw\",\"output_path\":\"output.png\",\"extra\":true}")]
    [Arguments("{\"prompt\":\"draw\",\"output_path\":\"output.png\",\"referenced_image_paths\":false}")]
    [Arguments("{\"prompt\":\"draw\",\"output_path\":\"output.png\",\"referenced_image_paths\":[null]}")]
    [Arguments("{\"prompt\":\"draw\",\"output_path\":\"output.png\",\"referenced_image_paths\":[1]}")]
    [Arguments("{\"prompt\":\"draw\",\"output_path\":\"output.png\",\"referenced_image_paths\":[\"  \"]}")]
    public async Task Rejects_malformed_arguments_without_provider_calls_or_mutation(string arguments, CancellationToken cancellationToken)
    {
        var outputPath = Path.Combine(_root, "output.png");
        await File.WriteAllTextAsync(outputPath, "original", cancellationToken);
        var provider = new ImageProvider("images", GeneratePng);
        ITool tool = new ImageGenerationTool(new ToolWorkspace(_root));
        var result = await tool.Execute(new ToolInvocation("generate", arguments), new TurnFixture(provider, WritableProfile()).Selection, cancellationToken);
        _ = await Assert.That(result.Text).StartsWith("error: ");
        _ = await Assert.That(result.ImageArtifacts).IsEmpty();
        _ = await Assert.That(provider.Calls).IsEqualTo(0);
        _ = await Assert.That(await File.ReadAllTextAsync(outputPath, cancellationToken)).IsEqualTo("original");
        _ = await Assert.That(Directory.GetFileSystemEntries(_root)).HasSingleItem();
    }

    [Test]
    [Arguments(5)]
    [Arguments(6)]
    public async Task Bounds_reference_count_and_preserves_order(int referenceCount, CancellationToken cancellationToken)
    {
        var references = new List<string>();
        for (var index = 0; index < referenceCount; index++)
        {
            var reference = $"reference-{index}.png";
            references.Add(reference);
            await File.WriteAllBytesAsync(Path.Combine(_root, reference), [.. Png(), .. new byte[index]], cancellationToken);
        }

        var provider = new ImageProvider("images", async (request, token) =>
        {
            _ = await Assert.That(request.References.Count).IsEqualTo(referenceCount);
            for (var index = 0; index < referenceCount; index++)
            {
                var expected = await File.ReadAllBytesAsync(Path.Combine(_root, references[index]), token);
                _ = await Assert.That(request.References[index].Data.SequenceEqual(expected)).IsTrue();
            }

            return new ImageGenerationResult(Png());
        });
        ITool tool = new ImageGenerationTool(new ToolWorkspace(_root));
        var result = await tool.Execute(new ToolInvocation("generate", EncodeArguments("draw", "output.png", references)), new TurnFixture(provider, WritableProfile()).Selection, cancellationToken);
        _ = await Assert.That(result.Text.StartsWith("error: ", StringComparison.Ordinal)).IsEqualTo(referenceCount > 5);
        _ = await Assert.That(provider.Calls).IsEqualTo(referenceCount > 5 ? 0 : 1);
        _ = await Assert.That(File.Exists(Path.Combine(_root, "output.png"))).IsEqualTo(referenceCount <= 5);
    }

    [Test]
    [Arguments("missing")]
    [Arguments("invalid")]
    [Arguments("oversized")]
    [Arguments("directory")]
    [Arguments("symlink")]
    [Arguments("denied")]
    [Arguments("fifo")]
    public async Task Rejects_unreadable_or_invalid_references_before_provider_call(string kind, CancellationToken cancellationToken)
    {
        var reference = Path.Combine(_root, "reference.png");
        var output = Path.Combine(_root, "output.png");
        await File.WriteAllTextAsync(output, "original", cancellationToken);
        var security = WritableProfile();
        switch (kind)
        {
            case "invalid":
                await File.WriteAllTextAsync(reference, "not an image", cancellationToken);
                break;
            case "oversized":
                await File.WriteAllBytesAsync(reference, new byte[(10 << 20) + 1], cancellationToken);
                break;
            case "directory":
                _ = Directory.CreateDirectory(reference);
                break;
            case "symlink":
                _ = File.CreateSymbolicLink(reference, output);
                break;
            case "denied":
                await File.WriteAllBytesAsync(reference, Png(), cancellationToken);
                security = SecurityProfile.Compose(false, [], [new SandboxRule(reference, SandboxRuleAction.DenyRead)], [new SandboxRule(output, SandboxRuleAction.AllowWrite)]);
                break;
            case "fifo":
                if (!OperatingSystem.IsLinux())
                {
                    return;
                }

                await MakeFifo(reference, cancellationToken);
                break;
        }

        var provider = new ImageProvider("images", GeneratePng);
        ITool tool = new ImageGenerationTool(new ToolWorkspace(_root));
        var result = await tool.Execute(new ToolInvocation("generate", EncodeArguments("draw", "output.png", ["reference.png"])), new TurnFixture(provider, security).Selection, cancellationToken);
        _ = await Assert.That(result.Text).StartsWith("error: ");
        _ = await Assert.That(provider.Calls).IsEqualTo(0);
        _ = await Assert.That(await File.ReadAllTextAsync(output, cancellationToken)).IsEqualTo("original");
    }

    [Test]
    [Arguments("denied")]
    [Arguments("directory")]
    [Arguments("symlink")]
    [Arguments("dangling")]
    [Arguments("linked-parent")]
    [Arguments("file-parent")]
    [Arguments("fifo")]
    public async Task Rejects_unauthorized_or_nonregular_outputs_before_provider_call(string kind, CancellationToken cancellationToken)
    {
        var output = Path.Combine(_root, "output.png");
        var target = Path.Combine(_root, "target.png");
        await File.WriteAllTextAsync(target, "original", cancellationToken);
        var security = WritableProfile();
        switch (kind)
        {
            case "denied":
                security = SecurityProfile.Compose(true, [], [], []);
                break;
            case "directory":
                _ = Directory.CreateDirectory(output);
                break;
            case "symlink":
                _ = File.CreateSymbolicLink(output, target);
                break;
            case "dangling":
                _ = File.CreateSymbolicLink(output, Path.Combine(_root, "missing.png"));
                break;
            case "linked-parent":
                var directory = Directory.CreateDirectory(Path.Combine(_root, "physical"));
                _ = Directory.CreateSymbolicLink(Path.Combine(_root, "alias"), directory.FullName);
                output = Path.Combine(_root, "alias", "output.png");
                break;
            case "file-parent":
                output = Path.Combine(target, "output.png");
                break;
            case "fifo":
                if (!OperatingSystem.IsLinux())
                {
                    return;
                }

                await MakeFifo(output, cancellationToken);
                break;
        }

        var provider = new ImageProvider("images", GeneratePng);
        ITool tool = new ImageGenerationTool(new ToolWorkspace(_root));
        var result = await tool.Execute(new ToolInvocation("generate", EncodeArguments("draw", output, [])), new TurnFixture(provider, security).Selection, cancellationToken);
        _ = await Assert.That(result.Text).StartsWith("error: ");
        _ = await Assert.That(provider.Calls).IsEqualTo(0);
        _ = await Assert.That(await File.ReadAllTextAsync(target, cancellationToken)).IsEqualTo("original");
        _ = await Assert.That(File.Exists(Path.Combine(_root, "missing.png"))).IsFalse();
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Checks_missing_parent_permissions_without_creating_during_preflight(bool allowParents, CancellationToken cancellationToken)
    {
        var parent = Path.Combine(_root, "missing", "nested");
        var output = Path.Combine(parent, "output.png");
        var provider = new ImageProvider("images", async (_, _) =>
        {
            _ = await Assert.That(Directory.Exists(parent)).IsFalse();
            return new ImageGenerationResult(Png());
        });
        var security = SecurityProfile.Compose(true, [], [], [new SandboxRule(allowParents ? _root : output, SandboxRuleAction.AllowWrite)]);
        ITool tool = new ImageGenerationTool(new ToolWorkspace(_root));
        var result = await tool.Execute(new ToolInvocation("generate", EncodeArguments("draw", output, [])), new TurnFixture(provider, security).Selection, cancellationToken);
        _ = await Assert.That(result.Text.StartsWith("error: ", StringComparison.Ordinal)).IsEqualTo(!allowParents);
        _ = await Assert.That(provider.Calls).IsEqualTo(allowParents ? 1 : 0);
        _ = await Assert.That(Directory.Exists(parent)).IsEqualTo(allowParents);
        _ = await Assert.That(File.Exists(output)).IsEqualTo(allowParents);
    }

    [Test]
    public async Task Provider_failure_preserves_old_output_without_retry(CancellationToken cancellationToken)
    {
        var output = Path.Combine(_root, "output.png");
        await File.WriteAllTextAsync(output, "original", cancellationToken);
        var provider = new ImageProvider("images", (_, _) => throw new LLMProviderException("provider: invalid image response"));
        ITool tool = new ImageGenerationTool(new ToolWorkspace(_root));
        var result = await tool.Execute(new ToolInvocation("generate", EncodeArguments("draw", output, [])), new TurnFixture(new RetryingProvider(provider), WritableProfile()).Selection, cancellationToken);
        _ = await Assert.That(result.Text).StartsWith("error: ");
        _ = await Assert.That(result.ImageArtifacts).IsEmpty();
        _ = await Assert.That(provider.Calls).IsEqualTo(1);
        _ = await Assert.That(await File.ReadAllTextAsync(output, cancellationToken)).IsEqualTo("original");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Invalid_provider_image_preserves_old_output(bool oversized, CancellationToken cancellationToken)
    {
        var output = Path.Combine(_root, "output.png");
        await File.WriteAllTextAsync(output, "original", cancellationToken);
        var provider = new ImageProvider("images", (_, _) => Task.FromResult(new ImageGenerationResult(oversized ? new byte[(32 << 20) + 1] : [1, 2, 3])));
        ITool tool = new ImageGenerationTool(new ToolWorkspace(_root));
        var result = await tool.Execute(new ToolInvocation("generate", EncodeArguments("draw", output, [])), new TurnFixture(provider, WritableProfile()).Selection, cancellationToken);
        _ = await Assert.That(result.Text).StartsWith("error: ");
        _ = await Assert.That(result.ImageArtifacts).IsEmpty();
        _ = await Assert.That(provider.Calls).IsEqualTo(1);
        _ = await Assert.That(await File.ReadAllTextAsync(output, cancellationToken)).IsEqualTo("original");
    }

    [Test]
    public async Task Participates_in_normal_tool_filtering()
    {
        ITool tool = new ImageGenerationTool(new ToolWorkspace(_root));
        var snapshot = ToolSnapshot.Document([tool], [true], new TestToolDefinitionsFixture("imagegen").Definitions);
        _ = await Assert.That(snapshot.Find("imagegen")).IsSameReferenceAs(tool);
        _ = await Assert.That(snapshot.Only(["imagegen"]).Definitions).HasSingleItem();
        _ = await Assert.That(snapshot.Only(["read"]).Tools).IsEmpty();
        _ = await Assert.That(snapshot.Without(["imagegen"]).Tools).IsEmpty();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Cancellation_preserves_old_output(bool duringProvider, CancellationToken cancellationToken)
    {
        var output = Path.Combine(_root, "output.png");
        await File.WriteAllTextAsync(output, "original", cancellationToken);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var provider = new ImageProvider("images", async (_, token) =>
        {
            await cancellation.CancelAsync();
            token.ThrowIfCancellationRequested();
            return new ImageGenerationResult(Png());
        });
        if (!duringProvider)
        {
            await cancellation.CancelAsync();
        }

        ITool tool = new ImageGenerationTool(new ToolWorkspace(_root));
        _ = await Assert.That(async () => await tool.Execute(new ToolInvocation("generate", EncodeArguments("draw", output, [])), new TurnFixture(provider, WritableProfile()).Selection, cancellation.Token)).Throws<OperationCanceledException>();
        _ = await Assert.That(provider.Calls).IsEqualTo(duringProvider ? 1 : 0);
        _ = await Assert.That(await File.ReadAllTextAsync(output, cancellationToken)).IsEqualTo("original");
    }

    [Test]
    public async Task Rechecks_output_after_provider_replaces_it_with_a_symlink(CancellationToken cancellationToken)
    {
        var output = Path.Combine(_root, "output.png");
        var target = Path.Combine(_root, "target.png");
        await File.WriteAllTextAsync(output, "old output", cancellationToken);
        await File.WriteAllTextAsync(target, "private target", cancellationToken);
        var provider = new ImageProvider("images", async (request, token) =>
        {
            _ = request;
            await Task.Yield();
            token.ThrowIfCancellationRequested();
            File.Delete(output);
            _ = File.CreateSymbolicLink(output, target);
            return new ImageGenerationResult(Png());
        });
        ITool tool = new ImageGenerationTool(new ToolWorkspace(_root));
        var result = await tool.Execute(new ToolInvocation("generate", EncodeArguments("draw", output, [])), new TurnFixture(provider, WritableProfile()).Selection, cancellationToken);
        _ = await Assert.That(result.Text).StartsWith("error: ");
        _ = await Assert.That(result.ImageArtifacts).IsEmpty();
        _ = await Assert.That(provider.Calls).IsEqualTo(1);
        _ = await Assert.That(await File.ReadAllTextAsync(target, cancellationToken)).IsEqualTo("private target");
        _ = await Assert.That(new FileInfo(output).LinkTarget).IsEqualTo(target);
    }

    private static byte[] Png() => Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGP4z8DwHwAFAAH/iZk9HQAAAABJRU5ErkJggg==");

    private static Task<ImageGenerationResult> GeneratePng(ImageGenerationRequest request, CancellationToken cancellationToken)
    {
        _ = request;
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new ImageGenerationResult(Png()));
    }

    private static string EncodeArguments(string prompt, string outputPath, IReadOnlyList<string> references) =>
        $"{{\"prompt\":\"{JsonEncodedText.Encode(prompt)}\",\"output_path\":\"{JsonEncodedText.Encode(outputPath)}\",\"referenced_image_paths\":[{string.Join(',', references.Select(reference => $"\"{JsonEncodedText.Encode(reference)}\""))}]}}";

    private static async Task MakeFifo(string path, CancellationToken cancellationToken)
    {
        using var process = System.Diagnostics.Process.Start(new ProcessStartInfo("mkfifo", path)
        {
            RedirectStandardError = true,
            UseShellExecute = false,
        }) ?? throw new InvalidOperationException("Could not start mkfifo.");
        await process.WaitForExitAsync(cancellationToken);
        _ = await Assert.That(process.ExitCode).IsEqualTo(0);
    }

    private SecurityProfile WritableProfile() => SecurityProfile.ForAgent(SecurityProfile.Compose(false, [], [], []), [_root], _root, []);

    private sealed class TurnFixture
    {
        public TurnFixture(ILLMProvider provider, SecurityProfile security)
        {
            var model = new ProviderModel(provider, new LLMModel("model", provider.Id));
            Selection = new AgentTurnSelection(new ModelSelector(model.Selector), TestModels.Resolve(model), new TestProfileFixture().Mode, security);
        }

        public AgentTurnSelection Selection { get; }
    }

    private sealed class ImageProvider(string id, Func<ImageGenerationRequest, CancellationToken, Task<ImageGenerationResult>> generate) : ILLMProvider
    {
        public string Id => id;

        public int Calls { get; private set; }

        public Task<ImageGenerationResult> GenerateImage(ImageGenerationRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            return generate(request, cancellationToken);
        }

        public IReadOnlyList<LLMModel> SeedModels() => throw new NotSupportedException();

        public ValueTask<bool> HasCredential(CancellationToken cancellationToken) => ValueTask.FromResult(true);

        public Task<IReadOnlyList<LLMModel>> ListModels(CancellationToken cancellationToken) => throw new NotSupportedException();

        public IAsyncEnumerable<LLMEvent> Call(LLMRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
