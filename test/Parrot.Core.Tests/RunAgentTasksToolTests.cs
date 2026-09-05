using System.Collections.Concurrent;
using Parrot.Agent;
using Parrot.AgentTasks;
using Parrot.Config;
using Parrot.Events;
using Parrot.Llm;
using Parrot.Protocol;
using Parrot.Security;
using Parrot.Store;
using Parrot.Tools;

namespace Parrot.Core.Tests;

internal sealed class RunAgentTasksToolTests : IAsyncDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "parrot-run-agent-tasks-tool-tests", Guid.NewGuid().ToString("N"));

    private readonly SessionDatabase _database = SessionDatabase.Open(":memory:");

    private readonly EventBroker _broker = new();

    private readonly List<AgentTaskRunCatalog> _catalogs = [];

    public RunAgentTasksToolTests() => Directory.CreateDirectory(_root);

    public async ValueTask DisposeAsync()
    {
        foreach (var catalog in _catalogs)
        {
            await catalog.DisposeAsync().ConfigureAwait(false);
        }

        _broker.Dispose();
        _database.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    [Test]
    [Arguments("{}", "error: Tool arguments require a string 'path'.")]
    [Arguments("{\"path\":\"\"}", "error: Tool arguments require a nonblank string 'path'.")]
    [Arguments("{\"path\":\"missing.json\"}", "error: Source 'missing.json' is missing.")]
    public async Task Rejects_invalid_or_missing_paths(
        string arguments,
        string expected,
        CancellationToken cancellationToken)
    {
        var runtime = Runtime(cancellationToken);
        await using var registry = runtime.Registry;
        var tool = Tool(runtime);

        var result = await tool.Execute(new ToolInvocation("call", arguments), runtime.Selection, cancellationToken);

        _ = await Assert.That(result.Text).IsEqualTo(expected);
    }

    [Test]
    [Arguments("{}", "error: Tool arguments require a string 'path'.")]
    [Arguments("{\"path\":null}", "error: Tool arguments require a string 'path'.")]
    [Arguments("{\"artifact\":null}", "error: Tool arguments require exactly one nonblank 'path' or non-null 'artifact'.")]
    [Arguments("{\"path\":\"artifact.json\",\"artifact\":{}}", "error: Tool arguments require exactly one nonblank 'path' or non-null 'artifact'.")]
    [Arguments("{\"path\":\"artifact.json\",\"artifact\":null}", "error: Tool arguments require exactly one nonblank 'path' or non-null 'artifact'.")]
    [Arguments("{\"path\":\" \",\"artifact\":{}}", "error: Tool arguments require exactly one nonblank 'path' or non-null 'artifact'.")]
    [Arguments("{\"artifact\":\"invalid\"}", "error: artifact must be an object.")]
    [Arguments("{\"artifact\":{\"schema_version\":1,\"tasks\":[],\"unknown\":true}}", "error: Invalid JSON: UnmappedJsonProperty, unknown, Parrot.AgentTasks.AgentTaskArtifactWire")]
    public async Task Rejects_invalid_embedded_artifacts(
        string arguments,
        string expected,
        CancellationToken cancellationToken)
    {
        var runtime = Runtime(cancellationToken);
        await using var registry = runtime.Registry;
        var tool = Tool(runtime);

        var result = await tool.Execute(new ToolInvocation("call", arguments), runtime.Selection, cancellationToken);

        _ = await Assert.That(result.Text).IsEqualTo(expected);
    }

    [Test]
    public async Task Rejects_multi_root_embedded_artifacts_before_execution(CancellationToken cancellationToken)
    {
        const string arguments =
            "{\"artifact\":{\"schema_version\":1,\"tasks\":[{\"name\":\"first\",\"description\":\"First\",\"payload\":\"work\",\"acceptance_criteria\":\"Done\"},{\"name\":\"second\",\"description\":\"Second\",\"payload\":\"work\",\"acceptance_criteria\":\"Done\"}]}}";
        var provider = new AgentTaskQueueProvider([]);
        var runtime = Runtime(provider, cancellationToken);
        await using var registry = runtime.Registry;
        var tool = Tool(runtime);

        var result = await tool.Execute(new ToolInvocation("multi-root-call", arguments), runtime.Selection, cancellationToken);

        _ = await Assert.That(result.Text).IsEqualTo("error: tasks must contain exactly one root task.");
        _ = await Assert.That(provider.Requests).IsEmpty();
    }

    [Test]
    public async Task Rejects_multi_root_path_artifacts_before_execution(CancellationToken cancellationToken)
    {
        await File.WriteAllTextAsync(
            Path.Combine(_root, "multi-root.json"),
            "{\"schema_version\":1,\"tasks\":[{\"name\":\"first\",\"description\":\"First\",\"payload\":\"work\",\"acceptance_criteria\":\"Done\"},{\"name\":\"second\",\"description\":\"Second\",\"payload\":\"work\",\"acceptance_criteria\":\"Done\"}]}",
            cancellationToken);
        var provider = new AgentTaskQueueProvider([]);
        var runtime = Runtime(provider, cancellationToken);
        await using var registry = runtime.Registry;
        var tool = Tool(runtime);

        var result = await tool.Execute(
            new ToolInvocation("multi-root-path-call", "{\"path\":\"multi-root.json\"}"),
            runtime.Selection,
            cancellationToken);

        _ = await Assert.That(result.Text).IsEqualTo("error: tasks must contain exactly one root task.");
        _ = await Assert.That(provider.Requests).IsEmpty();
    }

    [Test]
    public async Task Returns_after_admission_while_execution_uses_catalog_lifetime(CancellationToken cancellationToken)
    {
        const string arguments =
            "{\"artifact\":{\"schema_version\":1,\"tasks\":[{\"name\":\"leaf\",\"description\":\"Leaf\",\"payload\":\"work\",\"acceptance_criteria\":\"Done\"}]}}";
        using var provider = new AgentTaskBlockingProvider();
        var runtime = Runtime(provider, cancellationToken);
        await using var registry = runtime.Registry;
        var tool = Tool(runtime);
        using var invocation = new CancellationTokenSource();

        var result = await tool.Execute(new ToolInvocation("background-call", arguments), runtime.Selection, invocation.Token);
        await provider.WaitUntilArrived(cancellationToken);
        await invocation.CancelAsync();

        _ = await Assert.That(result.Text).Contains("background-call started in the background (name: leaf)");
        var admittedSnapshot = runtime.Runs.Snapshot().Single();
        _ = await Assert.That(admittedSnapshot.DisplayName).IsEqualTo("leaf");
        _ = await Assert.That(admittedSnapshot.Progress.Revision).IsGreaterThanOrEqualTo(1UL);
        _ = await Assert.That(admittedSnapshot.Progress.RootNodes.Single().Name).IsEqualTo("leaf");
        _ = await Assert.That(runtime.Completion.IsCompleted("background-call")).IsFalse();

        await runtime.Catalog.Settle();
        var terminal = await runtime.Completion.Wait("background-call", cancellationToken);
        _ = await Assert.That(terminal.Status).IsEqualTo(AgentTaskExecutionStatus.Canceled);
        _ = await Assert.That(runtime.Runs.Snapshot()).IsEmpty();
    }

    [Test]
    public async Task Configured_attempt_budget_limits_embedded_artifact_execution(CancellationToken cancellationToken)
    {
        const string arguments =
            "{\"artifact\":{\"schema_version\":1,\"tasks\":[{\"name\":\"leaf\",\"description\":\"Leaf\",\"payload\":\"work\",\"acceptance_criteria\":\"Done\"}]}}";
        var provider = new AgentTaskQueueProvider([
            "{\"result\":\"ready\",\"verdict\":\"reject_and_retry\",\"feedback\":\"try again\",\"payload\":\"again\"}",
            "{\"result\":\"ready\",\"verdict\":\"reject_and_retry\",\"feedback\":\"try again\",\"payload\":\"again\"}",
        ]);
        var runtime = Runtime(provider, cancellationToken);
        await using var registry = runtime.Registry;
        var tool = ToolWithAttempts(runtime, 2);

        var result = await tool.Execute(new ToolInvocation("retry-call", arguments), runtime.Selection, cancellationToken);
        var terminal = await runtime.Completion.Wait("retry-call", cancellationToken);

        _ = await Assert.That(result.Text).Contains("retry-call started in the background");
        using var document = System.Text.Json.JsonDocument.Parse(terminal.Result);
        var task = document.RootElement.GetProperty("tasks")[0];
        _ = await Assert.That(task.GetProperty("attempt_count").GetInt32()).IsEqualTo(2);
        _ = await Assert.That(task.GetProperty("result").GetString()).IsEqualTo("ready");
        _ = await Assert.That(task.GetProperty("verdict").GetString()).IsEqualTo("reject_and_retry");
        _ = await Assert.That(string.Join(",", task.GetProperty("retry_feedback").EnumerateArray().Select(item => item.GetString())))
            .IsEqualTo("try again,try again");
        _ = await Assert.That(runtime.Sessions.ProfileIds.Single()).IsEqualTo("agent-task-payload");
        _ = await Assert.That(task.GetProperty("evidence").ValueKind).IsEqualTo(System.Text.Json.JsonValueKind.Null);
        _ = await Assert.That(task.GetProperty("task_patch").ValueKind).IsEqualTo(System.Text.Json.JsonValueKind.Null);
        _ = await Assert.That(task.GetProperty("execution").ValueKind).IsEqualTo(System.Text.Json.JsonValueKind.Null);
        _ = await Assert.That(provider.Requests).Count().IsEqualTo(2);
        _ = await Assert.That(string.Join(",", provider.Requests.Select(request => request.Messages.Count(message => message.Role != LLMRole.System))))
            .IsEqualTo("1,3");
        _ = await Assert.That(provider.Requests.All(request =>
            request.Messages.Select(message => message.Content).Any(content => content.Contains("AgentTask role: payload executor", StringComparison.Ordinal))
            && request.Messages.Select(message => message.Content).Any(content => content.Contains("Inspect, implement, and verify this instruction:", StringComparison.Ordinal))))
            .IsTrue();
        _ = await Assert.That(provider.Requests[1].Messages.Last(message => message.Role == LLMRole.User).Content)
            .Contains("ready");
        _ = await Assert.That(provider.Requests[1].Messages.Last(message => message.Role == LLMRole.User).Content)
            .Contains("try again");
        _ = await Assert.That(provider.Requests.Any(request => request.Messages.Select(message => message.Content)
            .Any(content => content.Contains("agent-task-validation", StringComparison.Ordinal)))).IsFalse();
        _ = await Assert.That(provider.Requests.Count(request => request.Messages.Select(message => message.Content)
            .Any(content => content.Contains("AgentTask role: prepare", StringComparison.Ordinal)))).IsEqualTo(0);
    }

    [Test]
    [Arguments(true, 3)]
    [Arguments(false, 1)]
    public async Task Configured_history_fork_inherits_only_prior_root_history(
        bool forkParentHistory,
        int expectedMessageCount,
        CancellationToken cancellationToken)
    {
        const string arguments =
            "{\"artifact\":{\"schema_version\":1,\"tasks\":[{\"name\":\"leaf\",\"description\":\"Leaf\",\"payload\":\"work\",\"acceptance_criteria\":\"Done\"}]}}";
        var provider = new AgentTaskQueueProvider([
            "{\"result\":\"ready\",\"verdict\":\"accept\",\"evidence\":\"done\"}",
        ]);
        var runtime = Runtime(provider, cancellationToken);
        await using var registry = runtime.Registry;
        AppendCompletedRootHistory(runtime, "prior user history", "prior assistant history");
        const long assistantSequence = 3;
        AppendCurrentToolBatch(runtime, assistantSequence, "history-call", arguments);
        var tool = ToolWithHistoryFork(runtime, forkParentHistory);

        var result = await tool.Execute(
            new ToolInvocation("history-call", arguments, assistantSequence),
            runtime.Selection,
            cancellationToken);
        var terminal = await runtime.Completion.Wait("history-call", cancellationToken);

        _ = await Assert.That(result.Text).Contains("history-call started in the background");
        _ = await Assert.That(terminal.Result).Contains("\"status\":\"succeeded\"");
        var request = provider.Requests.Single();
        _ = await Assert.That(request.Messages.Count(message => message.Role != LLMRole.System))
            .IsEqualTo(expectedMessageCount);
        _ = await Assert.That(request.Messages.Any(message => message.Content == "prior user history"))
            .IsEqualTo(forkParentHistory);
        _ = await Assert.That(request.Messages.Any(message => message.Content == "prior assistant history"))
            .IsEqualTo(forkParentHistory);
        _ = await Assert.That(request.Messages.SelectMany(message => message.ToolCalls)
            .Any(call => call.Id == "history-call")).IsFalse();
    }

    [Test]
    public async Task Leaf_retry_task_array_uses_composite_roles_and_retains_feedback(CancellationToken cancellationToken)
    {
        const string arguments =
            "{\"artifact\":{\"schema_version\":1,\"tasks\":[{\"name\":\"leaf\",\"description\":\"Leaf\",\"payload\":\"work\",\"acceptance_criteria\":\"Done\"}]}}";
        var provider = new AgentTaskQueueProvider([
            "{\"result\":\"leaf result\",\"verdict\":\"reject_and_retry\",\"feedback\":\"split it\",\"payload\":[{\"name\":\"child\",\"description\":\"Child\",\"payload\":\"child work\",\"acceptance_criteria\":\"Child done\"}],\"replacement_result\":\"replacement result\"}",
            "{\"context\":\"composite context\"}",
            "{\"result\":\"child context\",\"verdict\":\"accept\",\"evidence\":\"child done\"}",
            "{\"verdict\":\"accept\",\"evidence\":\"parent done\"}",
        ]);
        var runtime = Runtime(provider, cancellationToken);
        await using var registry = runtime.Registry;
        var tool = Tool(runtime);

        var result = await tool.Execute(new ToolInvocation("transition-call", arguments), runtime.Selection, cancellationToken);
        var terminal = await runtime.Completion.Wait("transition-call", cancellationToken);

        _ = await Assert.That(result.Text).Contains("transition-call started in the background");
        using var document = System.Text.Json.JsonDocument.Parse(terminal.Result);
        var task = document.RootElement.GetProperty("tasks")[0];
        _ = await Assert.That(document.RootElement.GetProperty("status").GetString()).IsEqualTo("succeeded");
        _ = await Assert.That(task.GetProperty("attempt_count").GetInt32()).IsEqualTo(2);
        _ = await Assert.That(task.GetProperty("context").GetString()).IsEqualTo("composite context");
        _ = await Assert.That(task.GetProperty("retry_feedback").EnumerateArray().Single().GetString()).IsEqualTo("split it");
        _ = await Assert.That(task.GetProperty("evidence").GetString()).IsEqualTo("parent done");
        _ = await Assert.That(task.GetProperty("tasks")[0].GetProperty("evidence").GetString()).IsEqualTo("child done");
        _ = await Assert.That(provider.Requests).Count().IsEqualTo(4);
        _ = await Assert.That(runtime.Sessions.ProfileIds.Count(profile => profile == "agent-task-prepare")).IsEqualTo(2);
        _ = await Assert.That(runtime.Sessions.ProfileIds.Count(profile => profile == "agent-task-validation")).IsEqualTo(0);
        var identities = runtime.Sessions.Identities;
        _ = await Assert.That(identities).Count().IsEqualTo(4);
        var composite = identities.Zip(runtime.Sessions.ProfileIds)
            .Last(agent => agent.Second == "agent-task-prepare").First;
        var child = identities.Single(identity => identity.Name == "child");
        _ = await Assert.That(composite.ParentSessionId).IsEqualTo(runtime.Parent.SessionId);
        _ = await Assert.That(child.ParentSessionId).IsEqualTo(composite.SessionId);
        _ = await Assert.That(provider.Requests[3].Messages.Count(message => message.Role != LLMRole.System)).IsEqualTo(3);
        _ = await Assert.That(provider.Requests[3].Messages.Select(message => message.Content)
            .Any(content => content.Contains("composite context", StringComparison.Ordinal))).IsTrue();
        var childPrompt = provider.Requests[2].Messages.Last(message => message.Role == LLMRole.User).Content;
        _ = await Assert.That(childPrompt).Contains("replacement result");
    }

    [Test]
    public async Task Executes_embedded_artifact_without_filesystem_access(CancellationToken cancellationToken)
    {
        const string arguments =
            "{\"artifact\":{\"schema_version\":1,\"tasks\":[{\"name\":\"leaf\",\"description\":\"Leaf\",\"payload\":\"work\",\"acceptance_criteria\":\"Done\"}]}}";
        var provider = new AgentTaskQueueProvider([
            "{\"result\":\"ready\",\"verdict\":\"accept\",\"evidence\":\"done\"}",
        ]);
        var runtime = Runtime(provider, cancellationToken);
        await using var registry = runtime.Registry;
        var tool = Tool(runtime);
        var denied = runtime.Selection with
        {
            SecurityProfile = SecurityProfile.Compose(false, [], [new SandboxRule(_root, SandboxRuleAction.DenyRead)], []),
        };

        var result = await tool.Execute(new ToolInvocation("embedded-call", arguments), denied, cancellationToken);
        var terminal = await runtime.Completion.Wait("embedded-call", cancellationToken);

        _ = await Assert.That(result.Text).Contains("embedded-call started in the background");
        using var document = System.Text.Json.JsonDocument.Parse(terminal.Result);
        _ = await Assert.That(document.RootElement.GetProperty("status").GetString()).IsEqualTo("succeeded");
        var task = document.RootElement.GetProperty("tasks")[0];
        _ = await Assert.That(task.GetProperty("result").GetString()).IsEqualTo("ready");
        _ = await Assert.That(task.GetProperty("verdict").GetString()).IsEqualTo("accept");
        _ = await Assert.That(task.GetProperty("evidence").GetString()).IsEqualTo("done");
        _ = await Assert.That(task.GetProperty("task_patch").ValueKind).IsEqualTo(System.Text.Json.JsonValueKind.Null);
        _ = await Assert.That(task.GetProperty("execution").ValueKind).IsEqualTo(System.Text.Json.JsonValueKind.Null);
        _ = await Assert.That(provider.Requests).Count().IsEqualTo(1);
        var prompt = provider.Requests.Single().Messages.Single(message => message.Role == LLMRole.User).Content;
        _ = await Assert.That(prompt).Contains("AgentTask role: payload executor");
        _ = await Assert.That(prompt).Contains("Inspect, implement, and verify this instruction:");
        _ = await Assert.That(runtime.Repository.Replay().Any(published =>
            published.PayloadCase == Event.PayloadOneofCase.AgentTaskProgressSnapshot
            && published.AgentTaskProgressSnapshot.OriginToolCallId == "embedded-call")).IsTrue();
    }

    [Test]
    public async Task Rejects_out_of_policy_artifacts(CancellationToken cancellationToken)
    {
        var artifact = Path.Combine(_root, "denied.json");
        await File.WriteAllTextAsync(artifact, "{}", cancellationToken);
        var runtime = Runtime(cancellationToken);
        await using var registry = runtime.Registry;
        var tool = Tool(runtime);
        var denied = runtime.Selection with
        {
            SecurityProfile = SecurityProfile.Compose(
                false,
                [],
                [new SandboxRule(artifact, SandboxRuleAction.DenyRead)],
                []),
        };

        var result = await tool.Execute(
            new ToolInvocation("call", "{\"path\":\"denied.json\"}"),
            denied,
            cancellationToken);

        _ = await Assert.That(result.Text).IsEqualTo("error: access denied");
    }

    [Test]
    public async Task Rejects_symbolic_link_artifacts(CancellationToken cancellationToken)
    {
        var artifact = Path.Combine(_root, "artifact.json");
        await File.WriteAllTextAsync(artifact, "{}", cancellationToken);
        _ = File.CreateSymbolicLink(Path.Combine(_root, "alias.json"), artifact);
        var runtime = Runtime(cancellationToken);
        await using var registry = runtime.Registry;
        var tool = Tool(runtime);

        var result = await tool.Execute(
            new ToolInvocation("call", "{\"path\":\"alias.json\"}"),
            runtime.Selection,
            cancellationToken);

        _ = await Assert.That(result.Text).IsEqualTo("error: Path 'alias.json' traverses a symbolic link.");
    }

    [Test]
    public async Task Persists_and_publishes_progress_for_successful_invocation(CancellationToken cancellationToken)
    {
        const string artifactJson =
            """
            {"schema_version":1,"tasks":[{"name":"leaf","description":"Leaf","payload":"work","acceptance_criteria":"Done"}]}
            """;
        await File.WriteAllTextAsync(
            Path.Combine(_root, "artifact.json"),
            artifactJson,
            cancellationToken);
        var provider = new AgentTaskQueueProvider([
            "{\"result\":\"ready\",\"verdict\":\"accept\",\"evidence\":\"done\"}",
        ]);
        var runtime = Runtime(provider, cancellationToken);
        await using var registry = runtime.Registry;
        using var subscription = _broker.Subscribe();
        var tool = Tool(runtime);
        var running = tool.Execute(
            new ToolInvocation("distinctive-call", "{\"path\":\"artifact.json\"}"),
            runtime.Selection,
            cancellationToken);

        var brokered = await ObserveProgress(
            subscription,
            runtime.Repository,
            "distinctive-call",
            3,
            cancellationToken);
        var result = await running;
        var terminal = await runtime.Completion.Wait("distinctive-call", cancellationToken);

        _ = await Assert.That(brokered[0].AgentTaskProgressSnapshot.Revision).IsEqualTo(1UL);
        _ = await Assert.That(brokered[0].AgentTaskProgressSnapshot.RootNodes.Single().Status)
            .IsEqualTo(AgentTaskProgressStatus.Pending);
        _ = await Assert.That(result.Text).Contains("distinctive-call started in the background");
        using var document = System.Text.Json.JsonDocument.Parse(terminal.Result);
        _ = await Assert.That(document.RootElement.GetProperty("status").GetString()).IsEqualTo("succeeded");
        _ = await Assert.That(document.RootElement.GetProperty("tasks")[0].GetProperty("name").GetString())
            .IsEqualTo("leaf");
        var durable = runtime.Repository.Replay()
            .Where(published => published.PayloadCase == Event.PayloadOneofCase.AgentTaskProgressSnapshot)
            .ToArray();
        _ = await Assert.That(string.Join(',', durable.Select(published => published.AgentTaskProgressSnapshot.Revision)))
            .IsEqualTo("1,2,3");
        _ = await Assert.That(durable.All(published =>
            published.AgentSessionId == runtime.Parent.SessionId
            && published.AgentTaskProgressSnapshot.OriginToolCallId == "distinctive-call"))
            .IsTrue();
        _ = await Assert.That(brokered).Count().IsEqualTo(3);
    }

    [Test]
    public async Task Rejects_malformed_artifacts_after_secure_read(CancellationToken cancellationToken)
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "artifact.json"), "{}", cancellationToken);
        var runtime = Runtime(cancellationToken);
        await using var registry = runtime.Registry;
        var tool = Tool(runtime);

        var result = await tool.Execute(
            new ToolInvocation("call", "{\"path\":\"artifact.json\"}"),
            runtime.Selection,
            cancellationToken);

        _ = await Assert.That(result.Text).IsEqualTo("error: schema_version is required.");
    }

    private static void AppendCompletedRootHistory(
        RuntimeContext runtime,
        string userContent,
        string assistantContent)
    {
        runtime.Repository.AppendConversation(
            Published(runtime, "prior-user"),
            ConversationOrigin.UserInput,
            LLMRole.User,
            [ConversationPart.TextPart(userContent)],
            [],
            string.Empty);
        runtime.Repository.AppendConversation(
            Published(runtime, "prior-assistant"),
            ConversationOrigin.Model,
            LLMRole.Assistant,
            [ConversationPart.TextPart(assistantContent)],
            [],
            string.Empty);
    }

    private static void AppendCurrentToolBatch(
        RuntimeContext runtime,
        long expectedAssistantSequence,
        string callId,
        string arguments)
    {
        runtime.Repository.AppendConversation(
            Published(runtime, "current-batch"),
            ConversationOrigin.Model,
            LLMRole.Assistant,
            [ConversationPart.TextPart(string.Empty)],
            [new LLMToolCall(callId, "run_agent_tasks", arguments)],
            string.Empty);
        var assistantSequence = runtime.Repository.Conversation(runtime.Parent.SessionId)[^1].Sequence;
        if (assistantSequence != expectedAssistantSequence)
        {
            throw new InvalidOperationException("The current test tool batch has an unexpected sequence.");
        }
    }

    private static Event Published(RuntimeContext runtime, string id) => new()
    {
        Id = id,
        AgentSessionId = runtime.Parent.SessionId,
    };

    private static async Task<Event[]> ObserveProgress(
        EventSubscription subscription,
        EventRepository repository,
        string originToolCallId,
        int count,
        CancellationToken cancellationToken)
    {
        var observed = new List<Event>(count);
        while (observed.Count < count)
        {
            var published = await subscription.Reader.ReadAsync(cancellationToken);
            if (published.PayloadCase != Event.PayloadOneofCase.AgentTaskProgressSnapshot
                || published.AgentTaskProgressSnapshot.OriginToolCallId != originToolCallId)
            {
                continue;
            }

            if (!repository.Replay().Any(committed => committed.Id == published.Id))
            {
                throw new InvalidOperationException("Broker published AgentTask progress before it was durable.");
            }

            observed.Add(published);
        }

        return [.. observed];
    }

    private RunAgentTasksTool Tool(RuntimeContext runtime) =>
        ToolWithAttempts(runtime, 5);

    private RunAgentTasksTool ToolWithAttempts(RuntimeContext runtime, int maximumAttempts) =>
        Tool(runtime, new AgentTaskConfig(maximumAttempts, false, TestModels.PromptTemplates));

    private RunAgentTasksTool ToolWithHistoryFork(RuntimeContext runtime, bool forkParentHistory) =>
        Tool(runtime, new AgentTaskConfig(5, forkParentHistory, TestModels.PromptTemplates));

    private RunAgentTasksTool Tool(RuntimeContext runtime, AgentTaskConfig configuration) => new(
        new ToolWorkspace(_root),
        runtime.Router,
        runtime.ParentScope,
        runtime.Runs,
        runtime.Completion,
        _broker,
        runtime.Repository,
        configuration);

    private RuntimeContext Runtime(CancellationToken cancellationToken) =>
        Runtime(new AgentTaskQueueProvider([]), cancellationToken);

    private RuntimeContext Runtime(ILLMProvider provider, CancellationToken cancellationToken)
    {
        var model = new LLMModel("model", provider.Id);
        var providers = new ProviderRegistry(
            [provider],
            new Dictionary<string, IReadOnlyList<LLMModel>>(StringComparer.Ordinal) { [provider.Id] = [model] });
        var router = new ModelRouter(providers, new ModelAliasCatalog(providers, []), $"{provider.Id}/model");
        var repository = new EventRepository(_database);
        var sessions = new AgentTaskTestSessionFactory(router);
        var registry = TestModels.Registry(sessions, _broker, repository, TestModels.ProfileRegistry(), TestModels.PromptTemplates, cancellationToken);
        var identity = AgentIdentity.Main("tool-parent", "parent", TestModels.PromptTemplates);
        using var dependencies = TestModels.Dependencies(identity, _broker, repository, cancellationToken);
        using var parentScope = TestAgentSessionScope.Build(identity, AgentSessionParentLink.Root(), registry, TestModels.PromptTemplates, (sessionParentScope, _, children, childQuestions) => new AgentSession(
            identity,
            sessionParentScope,
            new ModelSelector($"{provider.Id}/model"),
            router,
            _broker,
            repository,
            [],
            TestModels.EmptyToolDefinitions,
            TestModels.MaterializePrompt(identity, _root, _root),
            new ToolOutputBlobStore(_root),
            TestModels.CompactionGroupBlobs(),
            new Parrot.Context.Compactor(90, 30, 60_000, 1024, TestModels.PromptTemplates),
            new Parrot.Context.ContextCadence(),
            TestModels.PromptTemplates,
            childQuestions,
            dependencies.ExitReminder,
            dependencies.Profile,
            TestModels.CompletionCallbacks(
                childQuestions,
                dependencies.ActiveWorkReminder,
                dependencies.ExitReminder,
                repository,
                _broker),
            SecurityProfileTestFactory.Create(SecurityProfile.Compose(false, [], [], [])),
            dependencies.Status,
            dependencies.Queues,
            new AgentSessionActivity(TimeProvider.System),
            cancellationToken));
        registry.RegisterRootScope(parentScope);
        var parent = parentScope.Session;
        var selected = parent.Selection();
        var catalog = new AgentTaskRunCatalog(cancellationToken);
        _catalogs.Add(catalog);
        return new RuntimeContext(
            router,
            sessions,
            registry,
            parentScope,
            parent,
            repository,
            catalog,
            catalog.Prepare(parent.SessionId),
            new Completion(),
            new AgentTurnSelection(
                selected.RequestedModel,
                router.Resolve(selected.RequestedModel.Value),
                selected.Profile,
                selected.SecurityProfile));
    }

    private sealed class RuntimeContext
    {
        internal RuntimeContext(
            ModelRouter router,
            AgentTaskTestSessionFactory sessions,
            AgentRegistry registry,
            IAgentSessionScope parentScope,
            IAgentSession parent,
            EventRepository repository,
            AgentTaskRunCatalog catalog,
            AgentTaskRunOwner runs,
            Completion completion,
            AgentTurnSelection selection)
        {
            Router = router;
            Sessions = sessions;
            Registry = registry;
            ParentScope = parentScope;
            Parent = parent;
            Repository = repository;
            Catalog = catalog;
            Runs = runs;
            Completion = completion;
            Selection = selection;
        }

        internal ModelRouter Router { get; }

        internal AgentTaskTestSessionFactory Sessions { get; }

        internal AgentRegistry Registry { get; }

        internal IAgentSessionScope ParentScope { get; }

        internal IAgentSession Parent { get; }

        internal EventRepository Repository { get; }

        internal AgentTaskRunCatalog Catalog { get; }

        internal AgentTaskRunOwner Runs { get; }

        internal Completion Completion { get; }

        internal AgentTurnSelection Selection { get; }
    }

    private sealed class Completion : IAgentTaskRunCompletion
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource<AgentTaskRunTerminal>> _deliveries = [];

        public Task Deliver(AgentTaskRunTerminal terminal, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            _ = Delivery(terminal.RunId).TrySetResult(terminal);
            return Task.CompletedTask;
        }

        public Task DeliverDuringShutdown(AgentTaskRunTerminal terminal, CancellationToken cancellationToken) =>
            Deliver(terminal, cancellationToken);

        internal bool IsCompleted(string runId) => Delivery(runId).Task.IsCompleted;

        internal Task<AgentTaskRunTerminal> Wait(string runId, CancellationToken cancellationToken) =>
            Delivery(runId).Task.WaitAsync(cancellationToken);

        private TaskCompletionSource<AgentTaskRunTerminal> Delivery(string runId) =>
            _deliveries.GetOrAdd(
                runId,
                static _ => new TaskCompletionSource<AgentTaskRunTerminal>(TaskCreationOptions.RunContinuationsAsynchronously));
    }
}
