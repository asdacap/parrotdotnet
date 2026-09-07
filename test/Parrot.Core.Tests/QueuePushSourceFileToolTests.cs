using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Parrot.Agent;
using Parrot.Llm;
using Parrot.Queues;
using Parrot.Security;
using Parrot.Tools;

namespace Parrot.Core.Tests;

internal sealed partial class QueuePushSourceFileToolTests : IDisposable
{
    private const int MaximumSourceFileBytes = 16 << 20;
    private readonly string _root = Directory.CreateDirectory(Path.Combine(
        Path.GetTempPath(),
        "parrot-queue-push-source-file-tests",
        Guid.NewGuid().ToString("n"))).FullName;

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Test]
    public async Task Loads_nonblank_UTF8_lines_and_preserves_retained_text(CancellationToken cancellationToken)
    {
        var source = Path.Combine(_root, "items.txt");
        await File.WriteAllTextAsync(
            source,
            "\uFEFFfirst\r\n\r\n  \r\n second \nthird\n",
            new UTF8Encoding(false),
            cancellationToken);
        using var queues = CreateQueues("line-rules");
        _ = queues.Create("work", string.Empty);

        ITool tool = new QueuePushTool(queues, new ToolWorkspace(_root));
        var result = await tool.Execute(
            Invocation("work", "items.txt", "back", close: false),
            new PushTurnFixture(Permissive()).Selection,
            cancellationToken);
        var taken = queues.TryTake("work", 10, QueueDirection.Front);

        _ = await Assert.That(result.Text).Contains("\"size\":3");
        _ = await Assert.That(string.Join('|', taken.Items)).IsEqualTo("first| second |third");
    }

    [Test]
    public async Task Loads_authorized_absolute_files_and_applies_front_direction_and_close(
        CancellationToken cancellationToken)
    {
        var external = Directory.CreateDirectory(Path.Combine(_root, "external"));
        var source = Path.Combine(external.FullName, "items.txt");
        await File.WriteAllTextAsync(source, "one\ntwo\n", cancellationToken);
        var workspace = Directory.CreateDirectory(Path.Combine(_root, "workspace"));
        using var queues = CreateQueues("absolute-close");
        _ = queues.Create("work", string.Empty);

        ITool tool = new QueuePushTool(queues, new ToolWorkspace(workspace.FullName));
        var result = await tool.Execute(
            Invocation("work", source, "front", close: true),
            new PushTurnFixture(Permissive()).Selection,
            cancellationToken);
        var taken = queues.TryTake("work", 10, QueueDirection.Front);

        using var document = JsonDocument.Parse(result.Text);
        _ = await Assert.That(document.RootElement.GetProperty("closed").GetBoolean()).IsTrue();
        _ = await Assert.That(string.Join('|', taken.Items)).IsEqualTo("two|one");
        _ = await Assert.That(taken.Info?.Closed).IsTrue();
    }

    [Test]
    public async Task Empty_or_whitespace_only_source_matches_an_empty_inline_push(CancellationToken cancellationToken)
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "empty.txt"), " \n\t\r\n", cancellationToken);
        using var queues = CreateQueues("empty-close");
        _ = queues.Create("work", string.Empty);

        ITool tool = new QueuePushTool(queues, new ToolWorkspace(_root));
        var result = await tool.Execute(
            Invocation("work", "empty.txt", "back", close: true),
            new PushTurnFixture(Permissive()).Selection,
            cancellationToken);

        using var document = JsonDocument.Parse(result.Text);
        _ = await Assert.That(document.RootElement.GetProperty("size").GetInt32()).IsEqualTo(0);
        _ = await Assert.That(document.RootElement.GetProperty("closed").GetBoolean()).IsTrue();
        var completed = queues.TryTake("work", 1, QueueDirection.Front);
        _ = await Assert.That(completed.Acquired).IsTrue();
        _ = await Assert.That(completed.Items).IsEmpty();
    }

    [Test]
    public async Task Denied_direct_source_does_not_change_the_queue(CancellationToken cancellationToken)
    {
        var source = Path.Combine(_root, "denied.txt");
        await File.WriteAllTextAsync(source, "secret\n", cancellationToken);
        var security = SecurityProfile.Compose(
            readOnly: false,
            [],
            [new SandboxRule(source, SandboxRuleAction.DenyRead)],
            []);

        await AssertFailureLeavesQueueUnchanged("denied.txt", security, "error: access denied", cancellationToken);
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Denied_symlink_path_or_target_does_not_change_the_queue(
        bool denyAlias,
        CancellationToken cancellationToken)
    {
        var targetDirectory = Directory.CreateDirectory(Path.Combine(_root, "target"));
        await File.WriteAllTextAsync(Path.Combine(targetDirectory.FullName, "items.txt"), "secret\n", cancellationToken);
        var alias = Path.Combine(_root, "alias");
        _ = Directory.CreateSymbolicLink(alias, targetDirectory.FullName);
        var denied = denyAlias ? alias : targetDirectory.FullName;
        var security = SecurityProfile.Compose(
            readOnly: false,
            [],
            [new SandboxRule(denied, SandboxRuleAction.DenyRead)],
            []);

        await AssertFailureLeavesQueueUnchanged(
            Path.Combine("alias", "items.txt"),
            security,
            "error: access denied",
            cancellationToken);
    }

    [Test]
    public async Task Missing_or_directory_source_does_not_change_the_queue(CancellationToken cancellationToken)
    {
        _ = Directory.CreateDirectory(Path.Combine(_root, "directory"));

        await AssertFailureLeavesQueueUnchanged(
            "missing.txt",
            Permissive(),
            "error: no such file or directory",
            cancellationToken);
        await AssertFailureLeavesQueueUnchanged(
            "directory",
            Permissive(),
            "error: source_file must be a file",
            cancellationToken);
    }

    [Test]
    public async Task Binary_or_invalid_UTF8_source_does_not_change_the_queue(CancellationToken cancellationToken)
    {
        await File.WriteAllBytesAsync(Path.Combine(_root, "binary.txt"), [0x61, 0x00, 0x62], cancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(_root, "invalid.txt"), [0x61, 0xC3, 0x28], cancellationToken);

        await AssertFailureLeavesQueueUnchanged(
            "binary.txt",
            Permissive(),
            "error: binary file",
            cancellationToken);
        await AssertFailureLeavesQueueUnchanged(
            "invalid.txt",
            Permissive(),
            "error: source_file is not valid UTF-8",
            cancellationToken);
    }

    [Test]
    public async Task Source_over_16_MiB_does_not_change_the_queue(CancellationToken cancellationToken)
    {
        var source = Path.Combine(_root, "large.txt");
        await using (var stream = File.Create(source))
        {
            stream.SetLength(MaximumSourceFileBytes + 1L);
        }

        await AssertFailureLeavesQueueUnchanged(
            "large.txt",
            Permissive(),
            $"error: source_file exceeds {MaximumSourceFileBytes} bytes",
            cancellationToken);
    }

    [Test]
    public async Task Final_queue_limit_failure_does_not_change_existing_items(CancellationToken cancellationToken)
    {
        var source = Path.Combine(_root, "escaped.txt");
        await File.WriteAllTextAsync(source, new string('\\', 9 << 20), cancellationToken);
        using var queues = CreateQueues("final-limit");
        _ = queues.Create("work", string.Empty);
        _ = await queues.Push("work", ["existing"], QueueDirection.Back, false, cancellationToken);

        ITool tool = new QueuePushTool(queues, new ToolWorkspace(_root));
        var result = await tool.Execute(
            Invocation("work", "escaped.txt", "back", close: true),
            new PushTurnFixture(Permissive()).Selection,
            cancellationToken);
        var taken = queues.TryTake("work", 10, QueueDirection.Front);

        _ = await Assert.That(result.Text).IsEqualTo($"error: queue: file exceeds {MaximumSourceFileBytes} bytes");
        _ = await Assert.That(string.Join('|', taken.Items)).IsEqualTo("existing");
        _ = await Assert.That(taken.Info?.Closed).IsFalse();
    }

    [Test]
    public async Task Cancellation_propagates_without_changing_the_queue(CancellationToken cancellationToken)
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "items.txt"), "item\n", cancellationToken);
        using var queues = CreateQueues("cancel");
        _ = queues.Create("work", string.Empty);
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();

        ITool tool = new QueuePushTool(queues, new ToolWorkspace(_root));
        _ = await Assert.That(async () =>
                _ = await tool.Execute(
                    Invocation("work", "items.txt", "back", close: true),
                    new PushTurnFixture(Permissive()).Selection,
                    canceled.Token))
            .Throws<OperationCanceledException>();
        _ = await Assert.That(queues.Get("work").Size).IsEqualTo(0);
        _ = await Assert.That(queues.Get("work").Closed).IsFalse();
    }

    private static AgentQueues CreateQueues(string id) =>
        TestModels.Queues(AgentIdentity.Main($"queue-push-source-{id}", "main", TestModels.PromptTemplates));

    private static ToolInvocation Invocation(string name, string sourceFile, string direction, bool close) =>
        new(
            "push",
            JsonSerializer.Serialize(
                new SourceFileArguments
                {
                    Name = name,
                    SourceFile = sourceFile,
                    Direction = direction,
                    Close = close,
                },
                TestJsonContext.Default.SourceFileArguments));

    private static SecurityProfile Permissive() => SecurityProfile.Compose(false, [], [], []);

    private async Task AssertFailureLeavesQueueUnchanged(
        string sourceFile,
        SecurityProfile security,
        string expected,
        CancellationToken cancellationToken)
    {
        using var queues = CreateQueues(Guid.NewGuid().ToString("n"));
        _ = queues.Create("work", string.Empty);
        _ = await queues.Push("work", ["existing"], QueueDirection.Back, false, cancellationToken);

        ITool tool = new QueuePushTool(queues, new ToolWorkspace(_root));
        var result = await tool.Execute(
            Invocation("work", sourceFile, "back", close: true),
            new PushTurnFixture(security).Selection,
            cancellationToken);
        var taken = queues.TryTake("work", 10, QueueDirection.Front);

        _ = await Assert.That(result.Text).IsEqualTo(expected);
        _ = await Assert.That(string.Join('|', taken.Items)).IsEqualTo("existing");
        _ = await Assert.That(taken.Info?.Closed).IsFalse();
    }

    private sealed class PushTurnFixture
    {
        public PushTurnFixture(SecurityProfile securityProfile)
        {
            ILLMProvider provider = new UnusedProvider();
            var model = new ProviderModel(provider, new LLMModel("model", provider.Id));
            Selection = new AgentTurnSelection(
                new ModelSelector(model.Selector),
                TestModels.Resolve(model),
                new TestProfileFixture().Mode,
                securityProfile);
        }

        public AgentTurnSelection Selection { get; }
    }

    private sealed class SourceFileArguments
    {
        public required string Name { get; init; }

        public required string SourceFile { get; init; }

        public required string Direction { get; init; }

        public required bool Close { get; init; }
    }

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
    [JsonSerializable(typeof(SourceFileArguments))]
    private sealed partial class TestJsonContext : JsonSerializerContext;
}
