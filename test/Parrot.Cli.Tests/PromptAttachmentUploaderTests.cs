using Parrot.Agent;
using Parrot.Config;
using Parrot.Tools;

namespace Parrot.Cli.Tests;

internal sealed class PromptAttachmentUploaderTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "parrot-attachment-uploader-tests", Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Test]
    public async Task Sends_structured_text_without_legacy_text(CancellationToken cancellationToken)
    {
        _ = Directory.CreateDirectory(_root);
        var invoker = new ScriptedInvoker();
        var client = new Parrot.Protocol.Parrot.ParrotClient(invoker);
        var request = await new UploaderFixture(_root).Uploader.Prepare(
            client,
            "session-1",
            ModeRegistry.Build,
            "hello",
            TextWriter.Null,
            cancellationToken);

        request = request ?? throw new InvalidOperationException("The prompt was not prepared.");

        _ = await Assert.That(request.Text).IsEmpty();
        _ = await Assert.That(request.Parts).HasSingleItem();
        _ = await Assert.That(request.Parts[0].Text).IsEqualTo("hello");
    }

    [Test]
    public async Task Inspects_media_type_instead_of_using_extension(CancellationToken cancellationToken)
    {
        _ = Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "pixel.dat");
        await File.WriteAllBytesAsync(
            path,
            Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGP4z8DwHwAFAAH/iZk9HQAAAABJRU5ErkJggg=="),
            cancellationToken);
        var invoker = new ScriptedInvoker();
        var client = new Parrot.Protocol.Parrot.ParrotClient(invoker);
        var request = await new UploaderFixture(_root).Uploader.Prepare(
            client,
            "session-1",
            ModeRegistry.Build,
            "inspect @pixel.dat",
            TextWriter.Null,
            cancellationToken);

        request = request ?? throw new InvalidOperationException("The prompt was not prepared.");

        _ = await Assert.That(request.Text).IsEmpty();
        _ = await Assert.That(request.Parts).Count().IsEqualTo(2);
        _ = await Assert.That(invoker.UploadedAttachments[^1].Description.MediaType).IsEqualTo("image/png");
    }

    private sealed class UploaderFixture
    {
        public UploaderFixture(string root)
        {
            var profiles = new Dictionary<string, ProfileConfig>(StringComparer.Ordinal)
            {
                [ModeRegistry.Build] = new(string.Empty, string.Empty, null, 1, 1, false, false, true, false, []),
                [ModeRegistry.Plan] = new(string.Empty, string.Empty, null, 1, 1, false, false, true, false, []),
                [ModeRegistry.Query] = new(string.Empty, string.Empty, null, 1, 1, false, false, true, false, []),
            };
            Uploader = new PromptAttachmentUploader(
                new ToolWorkspace(root),
                new ModeRegistry(
                    new ProfileRegistry(profiles, [], [], new HashSet<string>(StringComparer.Ordinal)),
                    ModeRegistry.Build));
        }

        public PromptAttachmentUploader Uploader { get; }
    }
}
