using System.Threading.Channels;
using Parrot.Agent;
using Parrot.Context;
using Parrot.Llm;
using Parrot.Process;
using Parrot.Protocol;
using Parrot.Security;
using Parrot.State;
using Parrot.Store;
using Parrot.Tools;

namespace Parrot.Core.Tests;

internal sealed class MonitorToolTests : IDisposable
{
    private readonly string _workspace = Directory.CreateTempSubdirectory("parrot-monitor-tool-tests-").FullName;

    private readonly ProviderModel _model = new(new UnusedProvider(), new LLMModel("model", "unused"));

    public void Dispose()
    {
        if (Directory.Exists(_workspace))
        {
            Directory.Delete(_workspace, recursive: true);
        }
    }

    [Test]
    [Timeout(60_000)]
    [Arguments(
        """{"command":"printf 'a\\nb\\n'; sleep 1; echo c; echo hidden >&2; exit 3","description":"ticks","name":"watch"}""",
        false,
        new[] { "Monitor watch (ticks):\na\nb", "Monitor watch (ticks):\nc", "Monitor watch (ticks) exited with code 3. Events delivered: 3." })]
    [Arguments(
        """{"command":"echo ready; while true; do sleep 0.1; done","description":"ready","name":"watch"}""",
        true,
        new[] { "Monitor watch (ready):\nready", "Monitor watch (ready) exited with code 143. Events delivered: 1." })]
    [Arguments(
        """{"command":"while true; do sleep 0.1; done","description":"idle","name":"watch","timeout_ms":1000}""",
        false,
        new[] { "Monitor watch (idle) reached its 1000ms timeout and was stopped; start it again if you still need the watch. Events delivered: 0." })]
    [Arguments(
        """{"command":"seq 1 500; while true; do sleep 0.1; done","description":"flood","name":"watch"}""",
        false,
        new[] { "Monitor watch (flood) produced too many events and was stopped; start it again with a tighter filter. Events delivered: 0." })]
    public async Task Stdout_lines_stream_as_events_until_the_monitor_ends(
        string arguments,
        bool interrupt,
        string[] expected,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        await using var session = new RecordingSession();
        await using var processes = CreateOwner(session);
        var tool = CreateTool(processes, session);
        var interruptTool = new InterruptProcessToolFactory(processes, new ResourceResolverFixture(null, null), TestModels.ToolDefinitions).Create(session);

        var started = await tool.Execute(new ToolInvocation("monitor-call", arguments), Selection(), cancellationToken);
        _ = await Assert.That(started.YieldedProcess?.ActivityKind).IsEqualTo(ShellProcessActivityKind.Monitor);
        if (interrupt)
        {
            _ = await Assert.That(processes.CaptureInventory().Processes.Single().ActivityKind).IsEqualTo(ShellProcessActivityKind.Monitor);
        }

        var messages = new List<string>();
        while (messages.Count < expected.Length)
        {
            var (input, source) = await session.Messages.Reader.ReadAsync(cancellationToken);
            messages.Add(input.Content);
            _ = await Assert.That(input.Delivery).IsEqualTo(Delivery.Steer);
            _ = await Assert.That(source).IsEqualTo(
                messages.Count < expected.Length ? InputSource.Monitor : InputSource.Unspecified);
            if (interrupt && messages.Count == 1)
            {
                _ = await interruptTool.Execute(new ToolInvocation("interrupt-call", """{"name":"watch","signal":15}"""), Selection(), cancellationToken);
            }
        }

        _ = await Assert.That(started.Text).IsEqualTo(
            $"""
            Monitor watch started. Each stdout line arrives as a notification while you keep working; a running monitor does not keep this turn open. Stop it early with interrupt_process.
            stderr does not produce events and is written to {started.YieldedProcess?.StderrPath}
            """);
        _ = await Assert.That(processes.Active()).IsEmpty();
        _ = await Assert.That(string.Join("\n---\n", messages)).IsEqualTo(string.Join("\n---\n", expected));
    }

    [Test]
    [Arguments("""{"command":"true"}""", "error: description must not be empty")]
    [Arguments("""{"command":"","description":"d"}""", "error: no command given")]
    [Arguments("""{"command":"true","description":"d","name":" "}""", "error: process name must not be empty")]
    [Arguments("""{"command":"true","description":"d","timeout_ms":999}""", "error: Tool argument 'timeout_ms' must be between 1000 and 1800000.")]
    [Arguments("""{"command":"true","description":"d","timeout_ms":1800001}""", "error: Tool argument 'timeout_ms' must be between 1000 and 1800000.")]
    public async Task Invalid_arguments_are_rejected(string arguments, string expected, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        await using var session = new RecordingSession();
        await using var processes = CreateOwner(session);

        var result = await CreateTool(processes, session).Execute(new ToolInvocation("monitor-call", arguments), Selection(), cancellationToken);

        _ = await Assert.That(result.Text).IsEqualTo(expected);
        _ = await Assert.That(processes.Snapshot()).IsEmpty();
    }

    private ITool CreateTool(ShellProcessOwner processes, RecordingSession session) =>
        new MonitorToolFactory(
            processes,
            TestModels.PromptTemplates,
            new ToolOutputBlobStore(Path.Combine(_workspace, "blobs")),
            TestDiagnosticLog.Instance,
            TestModels.ToolDefinitions).Create(session);

    private ShellProcessOwner CreateOwner(RecordingSession session)
    {
        var resources = new UserSessionResources(
            new StatePaths(
                Path.Combine(_workspace, ".state"),
                Path.Combine(_workspace, ".config"),
                Path.Combine(_workspace, ".data")),
            UserSessionId.Parse($"session-{Guid.NewGuid():n}"),
            ProjectWorkspace.FromLaunchDirectory(_workspace));
        return new ShellProcessOwner(
            session.Identity,
            resources,
            new AgentPathEnvironment(resources, resources.AgentScratch(session.Identity.NamePath)),
            TestModels.Runner(CreateSandboxPassThrough()),
            TestDiagnosticLog.Instance,
            CancellationToken.None);
    }

    private AgentTurnSelection Selection() =>
        new(
            new ModelSelector(_model.Selector),
            TestModels.Resolve(_model),
            new TestProfileFixture().Profile,
            SecurityProfile.Compose(readOnly: false, [], [], []));

    private string CreateSandboxPassThrough()
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException();
        }

        var path = Path.Combine(_workspace, $"sandbox-{Guid.NewGuid():n}");
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

    private sealed class RecordingSession : IAgentSession
    {
        public Channel<(AdmittedInput Input, InputSource Source)> Messages { get; } = Channel.CreateUnbounded<(AdmittedInput, InputSource)>();

        public string SessionId => "agent";

        public string Name => "agent";

        public string ParentSessionId => string.Empty;

        public string ParentSessionName => string.Empty;

        public int Depth => 0;

        public AgentIdentity Identity { get; } = AgentIdentity.Main("agent", "agent", TestModels.PromptTemplates);

        public string OutputPath => throw new NotSupportedException();

        public AgentSessionActivity Activity { get; } = new(TimeProvider.System);

        public TurnInterruptionRequest TurnInterruption { get; } = new();

        public Task<(Admission Admission, bool FollowUp)> Send(
            IReadOnlyList<ConversationPart> parts,
            string messageId,
            Delivery delivery,
            IncomingActivity reason,
            CancellationToken cancellationToken)
        {
            var input = new AdmittedInput(messageId, messageId, parts, delivery);
            _ = Messages.Writer.TryWrite((input, reason.Source));
            return Task.FromResult((new Admission(input, null), true));
        }

        public AgentSessionStatisticsSnapshot CaptureStatistics() => throw new NotSupportedException();

        public void AddDescendantUsage(AgentUsageIncrement increment) => throw new NotSupportedException();

        public AgentSelection CurrentSelection() => throw new NotSupportedException();

        public void UseResolvedSelection(ResolvedModelSelection selectedModel) => throw new NotSupportedException();

        public void Recover() => throw new NotSupportedException();

        public void UpdateSelection(AgentSelection selection) => throw new NotSupportedException();

        public bool Wake(IncomingActivity? activity) => throw new NotSupportedException();

        public Task Interrupt(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task Compact(ContextSize? targetContextSize, CancellationToken cancellationToken) => throw new NotSupportedException();

        public AgentSelection ResolvePolicySelection() => throw new NotSupportedException();

        public AgentPolicyLineage ResolvePolicyLineage() => throw new NotSupportedException();

        public bool IsIdle() => throw new NotSupportedException();

        public bool IsActive() => throw new NotSupportedException();

        public Task<IncomingActivity?> WaitForIncomingInput(
            TimeSpan duration,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task SetExitReminder(string title, string description, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> ClearExitReminder(string title, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<AgentSendResult> SendTextMessage(string message, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<string> SendAndWaitForResult(
            IReadOnlyList<ConversationPart> parts,
            string messageId,
            AgentSelection? selection,
            Action<Admission>? admitted,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task Record(IReadOnlyList<ConversationPart> parts, string messageId, Delivery delivery, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<WaitAgentResult> Wait(int yieldAfterMilliseconds, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ContextSnapshot EstimateContext(AgentTurnSelection selection) => throw new NotSupportedException();

        public IReadOnlyList<LLMToolDefinition> AdvertisedToolDefinitions(AgentTurnSelection selection) => throw new NotSupportedException();

        public ContextSnapshot EstimateContextForTools(AgentTurnSelection selection, IReadOnlyList<LLMToolDefinition> tools) => throw new NotSupportedException();

        public ContextSnapshot EstimateContextAfterToolResult(AgentTurnSelection selection, string toolCallId, string result) => throw new NotSupportedException();

        public Task<ContextCompactionResult> CompactFromTool(AgentTurnSelection selection, ContextSize? targetContextSize, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
