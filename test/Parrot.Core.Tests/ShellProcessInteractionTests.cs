using Parrot.Agent;
using Parrot.Context;
using Parrot.Events;
using Parrot.Llm;
using Parrot.Process;
using Parrot.Security;
using Parrot.State;
using Parrot.Store;

namespace Parrot.Core.Tests;

internal sealed class ShellProcessInteractionTests : IDisposable
{
    private readonly string _workspace = Path.Combine(
        Path.GetTempPath(), "parrot-shell-interaction-tests", Guid.NewGuid().ToString("n"));

    public ShellProcessInteractionTests() => Directory.CreateDirectory(_workspace);

    public void Dispose()
    {
        if (Directory.Exists(_workspace))
        {
            Directory.Delete(_workspace, recursive: true);
        }
    }

    [Test]
    public async Task Pseudo_terminal_writes_input_and_consumes_each_output_segment_once(
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var lifetime = new CancellationTokenSource();
        using var events = new EventBroker();
        using var database = SessionDatabase.Open(":memory:");
        var resources = CreateResources();
        var agent = CreateAgent(events, database, resources.AgentScratch("agent").BlobDirectory, lifetime.Token);
        using var inventory = new ShellProcessInventory();
        var owner = new ShellProcessOwner(
            agent.SessionId,
            resources,
            resources.AgentScratch(agent.SessionId),
            new ProcessRunner(CreateSandboxPassThrough()),
            inventory,
            lifetime.Token);
        var process = owner.Start(
            "interactive",
            "printf first; IFS= read -r line; printf 'received:%s' \"$line\"; exit 7",
            ProcessEnvironmentOverrides.Empty,
            agent,
            SecurityProfile.Compose(readOnly: false, [], [], []),
            ShellProcessTerminalMode.PseudoTerminal);

        var initial = await process.Wait(TimeSpan.FromMilliseconds(500), cancellationToken);
        var completed = await owner.WriteStdin(
            "interactive",
            "hello\n",
            TimeSpan.FromSeconds(2),
            cancellationToken);

        _ = await Assert.That(initial.Running).IsTrue();
        _ = await Assert.That(initial.Output).Contains("first");
        _ = await Assert.That(initial.Output).DoesNotContain("READY");
        _ = await Assert.That(completed.Running).IsFalse();
        var result = completed.Result ?? throw new InvalidOperationException("Missing completed process result.");
        _ = await Assert.That(result.ExitCode).IsEqualTo(7);
        _ = await Assert.That(result.Stdout).Contains("received:hello");
        _ = await Assert.That(result.Stdout).DoesNotContain("first");
        _ = await Assert.That(result.Stdout).DoesNotContain("READY");

        await owner.Settle();
    }

    [Test]
    public async Task Completed_spilled_suffix_survives_prompt_transcript_cleanup(
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var lifetime = new CancellationTokenSource();
        using var events = new EventBroker();
        using var database = SessionDatabase.Open(":memory:");
        var resources = CreateResources();
        var agent = CreateAgent(events, database, resources.AgentScratch("agent").BlobDirectory, lifetime.Token);
        using var inventory = new ShellProcessInventory();
        var owner = new ShellProcessOwner(
            agent.SessionId,
            resources,
            resources.AgentScratch(agent.SessionId),
            new ProcessRunner(CreateSandboxPassThrough()),
            inventory,
            lifetime.Token);
        var process = owner.Start(
            "spill",
            "printf prefix; sleep 0.5; dd if=/dev/zero bs=70000 count=1 2>/dev/null | tr '\\0' x",
            ProcessEnvironmentOverrides.Empty,
            agent,
            SecurityProfile.Compose(readOnly: false, [], [], []),
            ShellProcessTerminalMode.PseudoTerminal);
        var initial = await process.Wait(TimeSpan.FromMilliseconds(100), cancellationToken);
        var completed = await owner.Claim("spill").Wait(null, cancellationToken);
        var result = completed.Result ?? throw new InvalidOperationException("Missing completed process result.");

        _ = await Assert.That(initial.Running).IsTrue();
        _ = await Assert.That(result.Spilled).IsTrue();
        await WaitForNoTranscriptSpools(resources.AgentScratch(agent.SessionId).BlobDirectory, cancellationToken);
        _ = await Assert.That(File.Exists(result.BlobPath)).IsTrue();
        _ = await Assert.That(Directory.EnumerateFiles(resources.AgentScratch(agent.SessionId).BlobDirectory, ".process-*.tmp")).IsEmpty();
        var durable = await File.ReadAllTextAsync(result.BlobPath, cancellationToken);
        _ = await Assert.That(durable).Contains("[stdout]\n");
        _ = await Assert.That(durable).DoesNotContain("prefix");
        var stdout = durable[(durable.IndexOf("[stdout]\n", StringComparison.Ordinal) + "[stdout]\n".Length)..];
        _ = await Assert.That(stdout.Length).IsEqualTo(70000);
        _ = await Assert.That(stdout.All(value => value == 'x')).IsTrue();

        await owner.Settle();
        _ = await Assert.That(File.Exists(result.BlobPath)).IsTrue();
    }

    [Test]
    public async Task Empty_input_polls_only_output_after_the_prior_cursor(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var lifetime = new CancellationTokenSource();
        using var events = new EventBroker();
        using var database = SessionDatabase.Open(":memory:");
        var resources = CreateResources();
        var agent = CreateAgent(events, database, resources.AgentScratch("agent").BlobDirectory, lifetime.Token);
        using var inventory = new ShellProcessInventory();
        var owner = new ShellProcessOwner(
            agent.SessionId,
            resources,
            resources.AgentScratch(agent.SessionId),
            new ProcessRunner(CreateSandboxPassThrough()),
            inventory,
            lifetime.Token);
        var process = owner.Start(
            "poll",
            "printf first; sleep 0.2; printf second; sleep 30",
            ProcessEnvironmentOverrides.Empty,
            agent,
            SecurityProfile.Compose(readOnly: false, [], [], []),
            ShellProcessTerminalMode.PseudoTerminal);

        var initial = await process.Wait(TimeSpan.FromMilliseconds(100), cancellationToken);
        var poll = await owner.WriteStdin("poll", string.Empty, TimeSpan.FromMilliseconds(500), cancellationToken);

        _ = await Assert.That(initial.Running).IsTrue();
        _ = await Assert.That(initial.Output).Contains("first");
        _ = await Assert.That(poll.Running).IsTrue();
        _ = await Assert.That(poll.Output).Contains("second");
        _ = await Assert.That(poll.Output).DoesNotContain("first");

        var claimed = owner.Claim("poll");
        await claimed.SendSignal(new ProcessSignal(9), cancellationToken);
        await lifetime.CancelAsync();
    }

    [Test]
    public async Task Completed_name_remains_reserved_until_its_result_is_consumed(
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var lifetime = new CancellationTokenSource();
        using var events = new EventBroker();
        using var database = SessionDatabase.Open(":memory:");
        var resources = CreateResources();
        var agent = CreateAgent(events, database, resources.AgentScratch("agent").BlobDirectory, lifetime.Token);
        using var inventory = new ShellProcessInventory();
        var owner = new ShellProcessOwner(
            agent.SessionId,
            resources,
            resources.AgentScratch(agent.SessionId),
            new ProcessRunner(CreateSandboxPassThrough()),
            inventory,
            lifetime.Token);
        var security = SecurityProfile.Compose(readOnly: false, [], [], []);
        var first = owner.Start(
            "reusable",
            "printf old",
            ProcessEnvironmentOverrides.Empty,
            agent,
            security,
            ShellProcessTerminalMode.Pipe);
        await WaitUntilCompleted(first, cancellationToken);

        _ = await Assert.That(() => owner.Start(
            "reusable",
            "printf premature",
            ProcessEnvironmentOverrides.Empty,
            agent,
            security,
            ShellProcessTerminalMode.Pipe))
            .Throws<InvalidOperationException>();

        var consumed = await first.Wait(null, cancellationToken);
        var replacement = owner.Start(
            "reusable",
            "printf new",
            ProcessEnvironmentOverrides.Empty,
            agent,
            security,
            ShellProcessTerminalMode.Pipe);
        var replaced = await replacement.Wait(null, cancellationToken);

        _ = await Assert.That(consumed.Result?.Stdout).IsEqualTo("old");
        _ = await Assert.That(replaced.Result?.Stdout).IsEqualTo("new");
        await owner.Settle();
    }

    [Test]
    public async Task Cancelled_wait_releases_the_exclusive_claim(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var lifetime = new CancellationTokenSource();
        using var events = new EventBroker();
        using var database = SessionDatabase.Open(":memory:");
        var resources = CreateResources();
        var agent = CreateAgent(events, database, resources.AgentScratch("agent").BlobDirectory, lifetime.Token);
        using var inventory = new ShellProcessInventory();
        var owner = new ShellProcessOwner(
            agent.SessionId,
            resources,
            resources.AgentScratch(agent.SessionId),
            new ProcessRunner(CreateSandboxPassThrough()),
            inventory,
            lifetime.Token);
        var process = owner.Start(
            "claimed",
            "sleep 30",
            ProcessEnvironmentOverrides.Empty,
            agent,
            SecurityProfile.Compose(readOnly: false, [], [], []),
            ShellProcessTerminalMode.Pipe);
        _ = await process.Wait(TimeSpan.Zero, cancellationToken);
        var claimed = owner.Claim("claimed");

        _ = await Assert.That(() => owner.Claim("claimed"))
            .Throws<InvalidOperationException>();
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();
        _ = await Assert.That(async () => await claimed.Wait(null, canceled.Token))
            .Throws<OperationCanceledException>();

        var reclaimed = owner.Claim("claimed");
        await reclaimed.SendSignal(new ProcessSignal(9), cancellationToken);
        await owner.Settle();
    }

    private static async Task WaitUntilCompleted(
        ManagedShellProcess process,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 200 && !process.Completed; attempt++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(5), cancellationToken);
        }

        _ = await Assert.That(process.Completed).IsTrue();
    }

    private static async Task WaitForNoTranscriptSpools(
        string directory,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            if (!Directory.EnumerateFiles(directory, ".process-*.tmp").Any())
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(5), cancellationToken);
        }
    }

    private UserSessionResources CreateResources() =>
        new(
            new StatePaths(
                Path.Combine(_workspace, ".state"),
                Path.Combine(_workspace, ".config"),
                Path.Combine(_workspace, ".data")),
            UserSessionId.Parse($"session-{Guid.NewGuid():n}"),
            ProjectWorkspace.FromLaunchDirectory(_workspace));

    private AgentSession CreateAgent(
        EventBroker events,
        SessionDatabase database,
        string blobDirectory,
        CancellationToken lifetime)
    {
        var model = new ProviderModel(new UnusedProvider(), new LLMModel("model", "unused"));
        var repository = new EventRepository(database);
        var identity = AgentIdentity.Main("agent", "agent");
        var dependencies = TestModels.Dependencies(identity, events, repository, lifetime);
        return new AgentSession(
            identity,
            new ModelSelector(model.Selector),
            TestModels.Route(model),
            events,
            repository,
            [],
            TestModels.EmptyToolDefinitions,
            TestModels.MaterializePrompt(identity, _workspace, _workspace),
            new TodoCollection("agent", repository, events),
            new ToolOutputBlobStore(blobDirectory),
            new Compactor(90, 30, 60_000, 1024),
            dependencies.ActiveWorkReminder,
            dependencies.Profile,
            SecurityProfileTestFactory.Create(SecurityProfile.Compose(readOnly: false, [], [], [])),
            dependencies.Status,
            dependencies.Registry,
            dependencies.Queues,
            new AgentSessionActivity(TimeProvider.System),
            lifetime);
    }

    private string CreateSandboxPassThrough()
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException();
        }

        var path = Path.Combine(_workspace, "sandbox");
        var script = "#!/bin/sh\nhelper=\nwhile [ \"$1\" != \"--\" ]; do\n"
            + "  if [ \"$1\" = \"--chdir\" ]; then shift; cd \"$1\" || exit; "
            + "elif [ \"$1\" = \"--setenv\" ]; then export \"$2=$3\"; shift 2; "
            + "elif [ \"$1\" = \"--ro-bind\" ] && [ \"$2\" = \"$3\" ] "
            + "&& [ \"$(basename \"$2\")\" = \"parrot-pty-attach\" ]; "
            + "then helper=$2; shift 2; fi\n  shift\ndone\nshift\n"
            + "if [ \"$1\" = \"$helper\" ] && [ -n \"$helper\" ]; then shift; exec \"$helper\" \"$@\"; fi\n"
            + "exec \"$@\"\n";
        File.WriteAllText(path, script);
        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }
}
