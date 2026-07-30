using Parrot.Agent;
using Parrot.Context;
using Parrot.Events;
using Parrot.Llm;
using Parrot.Permissions;
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
        var coordinator = new ShellProcessOwners(
            resources,
            new ProcessRunner(CreateSandboxPassThrough()),
            lifetime.Token);
        var firstAgent = CreateAgent("agent-1", model, events, repository, resources.BlobDirectory, lifetime.Token);
        var secondAgent = CreateAgent("agent-2", model, events, repository, resources.BlobDirectory, lifetime.Token);
        var first = coordinator.Prepare(firstAgent.SessionId);
        var second = coordinator.Prepare(secondAgent.SessionId);
        coordinator.Register(first);
        coordinator.Register(second);
        var security = SecurityProfile.Compose(readOnly: false, [], [], []);

        var firstProcess = first.Start(
            "shared",
            "sleep 30",
            ProcessEnvironmentOverrides.Empty,
            firstAgent,
            security,
            SandboxWriteGrantSnapshot.Empty);
        var secondProcess = second.Start(
            "shared",
            "sleep 30",
            ProcessEnvironmentOverrides.Empty,
            secondAgent,
            security,
            SandboxWriteGrantSnapshot.Empty);
        var firstOnlyProcess = first.Start(
            "first-only",
            "sleep 30",
            ProcessEnvironmentOverrides.Empty,
            firstAgent,
            security,
            SandboxWriteGrantSnapshot.Empty);
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
    public async Task Settling_atomically_rejects_new_owners()
    {
        using var lifetime = new CancellationTokenSource();
        var coordinator = new ShellProcessOwners(
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
        CancellationToken lifetime) =>
        new(
            AgentIdentity.Main(sessionId, sessionId),
            new ModelSelector(model.Selector),
            TestModels.Route(model),
            events,
            repository,
            [],
            TestModels.PromptProvider(_workspace, _workspace),
            new TodoCollection(sessionId, repository, events),
            new ToolOutputBlobStore(blobDirectory),
            new Compactor(120_000),
            null,
            SecurityProfile.Compose(readOnly: false, [], [], []),
            null,
            null,
            null,
            lifetime);

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
