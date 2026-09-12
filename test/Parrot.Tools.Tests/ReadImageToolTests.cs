using Parrot.Agent;
using Parrot.Diagnostics;
using Parrot.Llm;
using Parrot.Security;
using Parrot.State;
using Parrot.Store;
using Parrot.Tools;

namespace Parrot.Core.Tests;

internal sealed class ReadImageToolTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parrot-read-image-tool-tests", Guid.NewGuid().ToString("n"));
    private readonly SessionDatabase _database;
    private readonly IDiagnosticLog _diagnostics;
    private readonly SessionResourceLease _resources;
    private readonly UserSessionResources _sessionResources;

    public ReadImageToolTests()
    {
        _ = Directory.CreateDirectory(_root);
        _sessionResources = new UserSessionResources(
            new StatePaths(_root, _root, _root),
            UserSessionId.Parse("images"),
            ProjectWorkspace.FromLaunchDirectory(_root));
        _database = SessionDatabase.Open(_sessionResources.DatabasePath);
        _diagnostics = FileDiagnosticLog.OpenSession(_sessionResources, "test", TextWriter.Null, TimeProvider.System);
        _resources = SessionResourceLease.Own(_sessionResources, _database, _diagnostics);
    }

    public void Dispose()
    {
        _resources.Dispose();
        _diagnostics.Dispose();
        _database.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Test]
    public async Task Reads_an_authorized_image_into_an_ordered_artifact_result(CancellationToken cancellationToken)
    {
        var image = Path.Combine(_root, "pixel.png");
        await File.WriteAllBytesAsync(image, Png(), cancellationToken);
        ITool tool = new ReadImageTool(new ToolWorkspace(_root), _resources.Images);
        var result = await tool.Execute(
            new ToolInvocation("test-call", "{\"path\":\"pixel.png\"}"),
            new SelectionFixture(SecurityProfile.Compose(false, [], [], [])).Selection,
            cancellationToken);

        _ = await Assert.That(result.Text).IsEqualTo("image read");
        _ = await Assert.That(result.ImageArtifacts).Count().IsEqualTo(1);
        _ = await Assert.That(result.ImageArtifacts[0].DisplayName).IsEqualTo("pixel.png");
        _ = await Assert.That(result.ImageArtifacts[0].Origin).IsEqualTo("read_image");
        _ = await Assert.That(File.Exists(Path.Combine(_sessionResources.ArtifactDirectory, $"{result.ImageArtifacts[0].ArtifactId}.png"))).IsTrue();
    }

    [Test]
    public async Task Reads_renamed_identical_images_using_the_first_artifact(CancellationToken cancellationToken)
    {
        var imageBytes = Png();
        await File.WriteAllBytesAsync(Path.Combine(_root, "pixel.png"), imageBytes, cancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(_root, "renamed.png"), imageBytes, cancellationToken);
        ITool tool = new ReadImageTool(new ToolWorkspace(_root), _resources.Images);
        var selection = new SelectionFixture(SecurityProfile.Compose(false, [], [], [])).Selection;
        var originalResult = await tool.Execute(
            new ToolInvocation("original-call", "{\"path\":\"pixel.png\"}"),
            selection,
            cancellationToken);
        var renamedResult = await tool.Execute(
            new ToolInvocation("renamed-call", "{\"path\":\"renamed.png\"}"),
            selection,
            cancellationToken);

        _ = await Assert.That(originalResult.Text).IsEqualTo("image read");
        _ = await Assert.That(renamedResult.Text).IsEqualTo("image read");
        _ = await Assert.That(originalResult.ImageArtifacts).Count().IsEqualTo(1);
        _ = await Assert.That(renamedResult.ImageArtifacts).Count().IsEqualTo(1);
        var originalArtifact = originalResult.ImageArtifacts[0];
        var renamedArtifact = renamedResult.ImageArtifacts[0];
        _ = await Assert.That(renamedArtifact.ArtifactId).IsEqualTo(originalArtifact.ArtifactId);
        _ = await Assert.That(renamedArtifact).IsEqualTo(originalArtifact);
        _ = await Assert.That(renamedArtifact.DisplayName).IsEqualTo("pixel.png");
        _ = await Assert.That(renamedArtifact.Origin).IsEqualTo("read_image");

        await using var stored = _resources.Images.Open(renamedArtifact.ArtifactId);
        using var storedBytes = new MemoryStream();
        await stored.CopyToAsync(storedBytes, cancellationToken);
        _ = await Assert.That(Convert.ToHexString(storedBytes.ToArray())).IsEqualTo(Convert.ToHexString(imageBytes));
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    public async Task Enforces_original_byte_budget_and_latches_until_a_new_cycle(
        int extraBytes,
        CancellationToken cancellationToken)
    {
        var imageBytes = Png();
        await File.WriteAllBytesAsync(Path.Combine(_root, "pixel.png"), imageBytes, cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(_root, "invalid.txt"), "not an image", cancellationToken);
        var budget = new ToolCycleImageBudget((imageBytes.Length * 2) + extraBytes, TestModels.PromptTemplates);
        ITool tool = new ReadImageTool(new ToolWorkspace(_root), _resources.Images);
        var selection = new SelectionFixture(SecurityProfile.Compose(false, [], [], [])).Selection;
        var invalid = await tool.Execute(
            new ToolInvocation("invalid", "{\"path\":\"invalid.txt\"}") { ImageBudget = budget },
            selection,
            cancellationToken);
        _ = await Assert.That(invalid.ImageArtifacts).IsEmpty();
        _ = await Assert.That(budget.IsExceeded).IsFalse();

        for (var index = 0; index < 4; index++)
        {
            var result = await tool.Execute(
                new ToolInvocation($"read-{index}", "{\"path\":\"pixel.png\"}") { ImageBudget = budget },
                selection,
                cancellationToken);
            _ = await Assert.That(result.ImageArtifacts.Count).IsEqualTo(index < 2 ? 1 : 0);
            _ = await Assert.That(result.Outcome).IsEqualTo(
                index < 2 ? ToolExecutionOutcome.Finished : ToolExecutionOutcome.ImageBudgetExceeded);
        }

        var latched = await tool.Execute(
            new ToolInvocation("latched", "invalid json") { ImageBudget = budget },
            selection,
            cancellationToken);
        _ = await Assert.That(latched.Outcome).IsEqualTo(ToolExecutionOutcome.ImageBudgetExceeded);
        _ = await Assert.That(latched.Text).IsEqualTo(budget.DescribeFailure());
        _ = await Assert.That(latched.ImageArtifacts).IsEmpty();

        var nextCycle = await tool.Execute(
            new ToolInvocation("retry", "{\"path\":\"pixel.png\"}")
            {
                ImageBudget = new ToolCycleImageBudget(imageBytes.Length, TestModels.PromptTemplates),
            },
            selection,
            cancellationToken);
        _ = await Assert.That(nextCycle.ImageArtifacts).Count().IsEqualTo(1);
        _ = await Assert.That(nextCycle.Outcome).IsEqualTo(ToolExecutionOutcome.Finished);
    }

    [Test]
    public async Task Does_not_disclose_a_physically_denied_symlink(CancellationToken cancellationToken)
    {
        var privateDirectory = Directory.CreateDirectory(Path.Combine(_root, "private")).FullName;
        await File.WriteAllBytesAsync(Path.Combine(privateDirectory, "pixel.png"), Png(), cancellationToken);
        _ = File.CreateSymbolicLink(Path.Combine(_root, "alias.png"), Path.Combine(privateDirectory, "pixel.png"));
        var security = SecurityProfile.Compose(
            readOnly: false,
            [],
            [new SandboxRule(privateDirectory, SandboxRuleAction.DenyRead)],
            []);

        ITool tool = new ReadImageTool(new ToolWorkspace(_root), _resources.Images);
        var result = await tool.Execute(
            new ToolInvocation("test-call", "{\"path\":\"alias.png\"}"),
            new SelectionFixture(security).Selection,
            cancellationToken);

        _ = await Assert.That(result.Text).IsEqualTo("error: access denied");
        _ = await Assert.That(result.ImageArtifacts).IsEmpty();
    }

    [Test]
    public async Task Rejects_non_image_content(CancellationToken cancellationToken)
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "not-image.txt"), "not an image", cancellationToken);

        ITool tool = new ReadImageTool(new ToolWorkspace(_root), _resources.Images);
        var result = await tool.Execute(
            new ToolInvocation("test-call", "{\"path\":\"not-image.txt\"}"),
            new SelectionFixture(SecurityProfile.Compose(false, [], [], [])).Selection,
            cancellationToken);

        _ = await Assert.That(result.Text).StartsWith("error: The image content is invalid or unsupported.");
        _ = await Assert.That(result.ImageArtifacts).IsEmpty();
    }

    private static byte[] Png() => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGP4z8DwHwAFAAH/iZk9HQAAAABJRU5ErkJggg==");

    private sealed class SelectionFixture
    {
        public SelectionFixture(SecurityProfile securityProfile)
        {
            var model = new ProviderModel(new UnusedProvider(), new LLMModel("model", "unused"));
            Selection = new AgentTurnSelection(
                new ModelSelector(model.Selector),
                TestModels.Resolve(model),
                new TestProfileFixture().Mode,
                securityProfile);
        }

        public AgentTurnSelection Selection { get; }
    }
}
