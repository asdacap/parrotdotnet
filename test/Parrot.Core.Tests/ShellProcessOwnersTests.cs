using Parrot.Agent;
using Parrot.Context;
using Parrot.Events;
using Parrot.Llm;
using Parrot.Process;
using Parrot.Security;
using Parrot.State;
using Parrot.Store;

namespace Parrot.Core.Tests;

internal sealed class ShellProcessOwnersTests : IDisposable
{
    private readonly string _workspace = Path.Combine(
        Path.GetTempPath(), "parrot-shell-process-owner-tests", Guid.NewGuid().ToString("n"));

    public ShellProcessOwnersTests() => Directory.CreateDirectory(_workspace);

    public void Dispose()
    {
        if (Directory.Exists(_workspace))
        {
            Directory.Delete(_workspace, recursive: true);
        }
    }

    [Test]
    public async Task Owners_isolate_names_and_coordinator_qualifies_observations(
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var lifetime = new CancellationTokenSource();
        using var events = new EventBroker();
        using var database = SessionDatabase.Open(":memory:");
        var repository = new EventRepository(database);
        var model = new ProviderModel(new UnusedProvider(), new LLMModel("model", "unused"));
        var resources = CreateResources();
        using var coordinator = new ShellProcessOwners(
            resources,
            new ProcessRunner(CreateSandboxPassThrough()),
            lifetime.Token);
        await using var firstAgent = CreateAgent("agent-1", model, events, repository, resources.AgentScratch("agent-1").BlobDirectory, lifetime.Token);
        await using var secondAgent = CreateAgent("agent-2", model, events, repository, resources.AgentScratch("agent-2").BlobDirectory, lifetime.Token);
        var first = coordinator.Prepare(firstAgent.SessionId);
        var second = coordinator.Prepare(secondAgent.SessionId);
        coordinator.Register(first);
        coordinator.Register(second);
        var security = SecurityProfile.Compose(readOnly: false, [], [], []);

        var firstProcess = first.Start(
            "shared",
            "sleep 30",
            "call-id",
            ProcessEnvironmentOverrides.Empty,
            firstAgent,
            security,
            ShellProcessTerminalMode.Pipe);
        var secondProcess = second.Start(
            "shared",
            "sleep 30",
            "call-id",
            ProcessEnvironmentOverrides.Empty,
            secondAgent,
            security,
            ShellProcessTerminalMode.Pipe);
        var firstOnlyProcess = first.Start(
            "first-only",
            "sleep 30",
            "call-id",
            ProcessEnvironmentOverrides.Empty,
            firstAgent,
            security,
            ShellProcessTerminalMode.Pipe);
        _ = await firstProcess.Wait(TimeSpan.Zero, cancellationToken);
        _ = await secondProcess.Wait(TimeSpan.Zero, cancellationToken);
        _ = await firstOnlyProcess.Wait(TimeSpan.Zero, cancellationToken);

        _ = await Assert.That(() => second.Claim("first-only"))
            .Throws<InvalidOperationException>();
        var observations = coordinator.Active();
        _ = await Assert.That(string.Join('|', observations.Select(item => item.Id)))
            .IsEqualTo("agent-1/first-only|agent-1/shared|agent-2/shared");
        _ = await Assert.That(string.Join('|', observations.Select(item => item.Name)))
            .IsEqualTo("first-only|shared|shared");

        await lifetime.CancelAsync();
        await coordinator.Settle();
        _ = await Assert.That(() => coordinator.Prepare("agent-3"))
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task Owner_keeps_completed_process_active_until_its_result_is_committed(
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var lifetime = new CancellationTokenSource();
        using var events = new EventBroker();
        using var database = SessionDatabase.Open(":memory:");
        var repository = new EventRepository(database);
        var model = new ProviderModel(new UnusedProvider(), new LLMModel("model", "unused"));
        var resources = CreateResources();
        using var coordinator = new ShellProcessOwners(
            resources,
            new ProcessRunner(CreateSandboxPassThrough()),
            lifetime.Token);
        await using var agent = CreateAgent("agent-1", model, events, repository, resources.AgentScratch("agent-1").BlobDirectory, lifetime.Token);
        var owner = coordinator.Prepare(agent.SessionId);
        coordinator.Register(owner);
        var process = owner.Start(
            "completed-but-uncommitted",
            "true",
            "call-id",
            ProcessEnvironmentOverrides.Empty,
            agent,
            SecurityProfile.Compose(readOnly: false, [], [], []),
            ShellProcessTerminalMode.Pipe);

        while (!process.Completed)
        {
            await Task.Delay(10, cancellationToken);
        }

        _ = await Assert.That(owner.Active()).HasSingleItem();
        _ = await process.Wait(yieldAfter: null, cancellationToken);
        _ = await Assert.That(owner.Active()).IsEmpty();

        await coordinator.Settle();
    }

    [Test]
    public async Task Inventory_tracks_process_from_start_until_completion(
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var lifetime = new CancellationTokenSource();
        using var events = new EventBroker();
        using var database = SessionDatabase.Open(":memory:");
        var repository = new EventRepository(database);
        var model = new ProviderModel(new UnusedProvider(), new LLMModel("model", "unused"));
        var resources = CreateResources();
        using var coordinator = new ShellProcessOwners(
            resources,
            new ProcessRunner(CreateSandboxPassThrough()),
            lifetime.Token);
        await using var agent = CreateAgent("agent-1", model, events, repository, resources.AgentScratch("agent-1").BlobDirectory, lifetime.Token);
        var owner = coordinator.Prepare(agent.SessionId);
        coordinator.Register(owner);
        var completionMarker = Path.Combine(_workspace, "complete");
        var process = owner.Start(
            "visible-from-start",
            $"while [ ! -f '{completionMarker}' ]; do sleep 0.01; done",
            "call-id",
            ProcessEnvironmentOverrides.Empty,
            agent,
            SecurityProfile.Compose(readOnly: false, [], [], []));

        using var subscription = coordinator.SubscribeInventory();
        var initial = await subscription.Reader.ReadAsync(cancellationToken);

        _ = await Assert.That(initial.Processes.Count).IsEqualTo(1);
        _ = await Assert.That(initial.Processes[0].ProcessId).IsEqualTo(process.State.ProcessId);
        var yielded = await process.Wait(TimeSpan.Zero, cancellationToken);
        var yieldedProcess = yielded.YieldedProcess
            ?? throw new InvalidOperationException("The running process did not yield.");
        _ = await Assert.That(yieldedProcess.VisibleRevision).IsEqualTo(initial.Revision);

        var claimed = owner.Claim(process.Name);
        await File.WriteAllTextAsync(completionMarker, string.Empty, cancellationToken);
        _ = await claimed.Wait(null, cancellationToken);
        var completed = await subscription.Reader.ReadAsync(cancellationToken);

        _ = await Assert.That(completed.Revision).IsGreaterThan(initial.Revision);
        _ = await Assert.That(completed.Processes).IsEmpty();
        await coordinator.Settle();
    }

    [Test]
    public async Task Settling_atomically_rejects_new_owners()
    {
        using var lifetime = new CancellationTokenSource();
        using var coordinator = new ShellProcessOwners(
            CreateResources(),
            new ProcessRunner(string.Empty),
            lifetime.Token);

        var settlement = coordinator.Settle();

        _ = await Assert.That(() => coordinator.Prepare("late"))
            .Throws<InvalidOperationException>();
        await settlement;
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
        string sessionId,
        ProviderModel model,
        EventBroker events,
        EventRepository repository,
        string blobDirectory,
        CancellationToken lifetime)
    {
        var identity = AgentIdentity.Main(sessionId, sessionId, TestModels.PromptTemplates);
        using var dependencies = TestModels.Dependencies(identity, events, repository, lifetime);
        return new AgentSession(identity, AgentSessionParentScope.Root(), new ModelSelector(model.Selector), TestModels.Route(model), events, repository, [], TestModels.EmptyToolDefinitions, TestModels.MaterializePrompt(identity, _workspace, _workspace), new ToolOutputBlobStore(blobDirectory), TestModels.CompactionGroupBlobs(), new Compactor(90, 30, 60_000, 1024, TestModels.PromptTemplates), TestModels.PromptTemplates, dependencies.ChildQuestions, dependencies.ExitReminder, dependencies.Profile, TestModels.CompletionCallbacks(dependencies.ChildQuestions, dependencies.ActiveWorkReminder, dependencies.ExitReminder, repository, events), SecurityProfileTestFactory.Create(SecurityProfile.Compose(readOnly: false, [], [], [])), dependencies.Status, dependencies.ChildRegistry, dependencies.Queues, new AgentSessionActivity(TimeProvider.System), lifetime);
    }

    private string CreateSandboxPassThrough()
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException();
        }

        var path = Path.Combine(_workspace, "sandbox");
        var script = "#!/bin/sh\nwhile [ \"$1\" != \"--\" ]; do\n"
            + "  if [ \"$1\" = \"--chdir\" ]; then shift; cd \"$1\" || exit; "
            + "elif [ \"$1\" = \"--setenv\" ]; then export \"$2=$3\"; shift 2; fi\n"
            + "  shift\ndone\nshift\nexec \"$@\"\n";
        File.WriteAllText(path, script);
        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }
}
