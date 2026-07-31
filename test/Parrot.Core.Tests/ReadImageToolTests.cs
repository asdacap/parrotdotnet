using Parrot.Agent;
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
    private readonly SessionResourceLease _resources;

    public ReadImageToolTests()
    {
        _ = Directory.CreateDirectory(_root);
        var resources = Resources();
        _database = SessionDatabase.Open(resources.DatabasePath);
        _resources = SessionResourceLease.Own(resources, _database);
    }

    public void Dispose()
    {
        _resources.Dispose();
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
        var result = await Tool().Execute(
            new ToolInvocation("test-call", "{\"path\":\"pixel.png\"}"),
            Turn(Permissive()),
            cancellationToken);

        _ = await Assert.That(result.Text).IsEqualTo("image read");
        _ = await Assert.That(result.ImageArtifacts).Count().IsEqualTo(1);
        _ = await Assert.That(result.ImageArtifacts[0].DisplayName).IsEqualTo("pixel.png");
        _ = await Assert.That(result.ImageArtifacts[0].Origin).IsEqualTo("read_image");
        _ = await Assert.That(File.Exists(Path.Combine(Resources().ArtifactDirectory, $"{result.ImageArtifacts[0].ArtifactId}.png"))).IsTrue();
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

        var result = await Tool().Execute(
            new ToolInvocation("test-call", "{\"path\":\"alias.png\"}"),
            Turn(security),
            cancellationToken);

        _ = await Assert.That(result.Text).IsEqualTo("error: access denied");
        _ = await Assert.That(result.ImageArtifacts).IsEmpty();
    }

    [Test]
    public async Task Rejects_non_image_content(CancellationToken cancellationToken)
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "not-image.txt"), "not an image", cancellationToken);

        var result = await Tool().Execute(
            new ToolInvocation("test-call", "{\"path\":\"not-image.txt\"}"),
            Turn(Permissive()),
            cancellationToken);

        _ = await Assert.That(result.Text).StartsWith("error: The image content is invalid or unsupported.");
        _ = await Assert.That(result.ImageArtifacts).IsEmpty();
    }

    private static byte[] Png() => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGP4z8DwHwAFAAH/iZk9HQAAAABJRU5ErkJggg==");

    private static SecurityProfile Permissive() => SecurityProfile.Compose(false, [], [], []);

    private static AgentTurnSelection Turn(SecurityProfile securityProfile)
    {
        var model = new ProviderModel(new UnusedProvider(), new LLMModel("model", "unused"));
        return new AgentTurnSelection(
            new ModelSelector(model.Selector),
            TestModels.Resolve(model),
            TestModels.Profile(),
            securityProfile);
    }

    private ReadImageTool Tool() => new(new ToolWorkspace(_root), _resources.Images);

    private UserSessionResources Resources() => new(
        new StatePaths(_root, _root, _root),
        UserSessionId.Parse("images"),
        ProjectWorkspace.FromLaunchDirectory(_root));
}
