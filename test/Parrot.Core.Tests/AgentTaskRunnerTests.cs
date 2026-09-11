using Parrot.Agent;
using Parrot.AgentTasks;
using Parrot.Config;
using Parrot.Diagnostics;
using Parrot.Events;
using Parrot.Llm;
using Parrot.Protocol;
using Parrot.Security;
using Parrot.State;
using Parrot.Statuses;
using Parrot.Store;

namespace Parrot.Core.Tests;

internal sealed class AgentTaskRunnerTests : IAsyncDisposable
{
    private readonly SessionDatabase _database = SessionDatabase.Open(":memory:");
    private readonly EventBroker _broker = new();
    private readonly List<IAgentRegistry> _registries = [];
    private readonly EventRepository _repository;

    public AgentTaskRunnerTests() => _repository = new EventRepository(_database);

    public async ValueTask DisposeAsync()
    {
        foreach (var registry in _registries)
        {
            await registry.DisposeAsync().ConfigureAwait(false);
        }

        _broker.Dispose();
        _database.Dispose();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Child_scope_diagnostics_preserve_creation_result_and_hide_failure_payload(
        bool fail,
        CancellationToken cancellationToken)
    {
        var directory = Path.Combine(Path.GetTempPath(), "parrot-child-diagnostics", Guid.NewGuid().ToString("n"));
        _ = Directory.CreateDirectory(directory);
        try
        {
            var resources = new UserSessionResources(
                new StatePaths(Path.Combine(directory, "state"), Path.Combine(directory, "config"), Path.Combine(directory, "data")),
                UserSessionId.Parse("session-diagnostics"),
                ProjectWorkspace.FromLaunchDirectory(directory));
            using var diagnostics = FileDiagnosticLog.OpenSession(resources, "test", TextWriter.Null, TimeProvider.System);
            var runtime = Runtime(new AgentTaskQueueProvider([]), cancellationToken);
            await using var runtimeRegistry = runtime.Registry;
            var factory = new DiagnosticChildFactory(runtime.ParentScope, fail);
            await using IAgentRegistry registry = new AgentRegistry(
                factory,
                _broker,
                _repository,
                new TestProfileFixture().Registry,
                TestModels.PromptTemplates,
                new RetainedAgentBudget(10),
                diagnostics,
                cancellationToken);
            var identity = AgentIdentity.Child(
                "agent-child", runtime.Parent.SessionId, "secret-parent", "secret-child", 1, AgentScope.Empty(TestModels.PromptTemplates), TestModels.PromptTemplates);
            using var dependencies = TestModels.Dependencies(runtime.Parent.Identity, _broker, _repository, cancellationToken);
            IAgentSessionScope CreateChild() => registry.CreateChildScope(
                identity,
                AgentSessionParentLink.Child(runtime.ParentScope, AgentCompletionDeliveryPolicy.RetainedOnly),
                runtime.Selection.RequestedModel,
                dependencies.Profile,
                dependencies.Profile.Profile.SecurityProfile,
                dependencies.Status,
                _repository,
                cancellationToken);
            if (fail)
            {
                _ = await Assert.That(CreateChild).Throws<InvalidOperationException>();
            }
            else
            {
                _ = await Assert.That(CreateChild()).IsSameReferenceAs(runtime.ParentScope);
            }

            var log = await File.ReadAllTextAsync(resources.LogPath, cancellationToken);
            _ = await Assert.That(log).Contains("event=\"child_scope_start\"")
                .And.Contains("event=\"child_scope_completed\"")
                .And.Contains(fail ? "outcome=\"failed\"" : "outcome=\"succeeded\"")
                .And.Contains("agent=\"agent-child\"")
                .And.DoesNotContain("secret-");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    [Arguments(AgentTaskExecutionStatus.Succeeded)]
    [Arguments(AgentTaskExecutionStatus.Failed)]
    [Arguments(AgentTaskExecutionStatus.Canceled)]
    public async Task Task_diagnostics_record_safe_scheduling_and_terminal_transitions(AgentTaskExecutionStatus status)
    {
        var directory = Path.Combine(Path.GetTempPath(), "parrot-task-diagnostics", Guid.NewGuid().ToString("n"));
        _ = Directory.CreateDirectory(directory);
        try
        {
            var resources = new UserSessionResources(
                new StatePaths(Path.Combine(directory, "state"), Path.Combine(directory, "config"), Path.Combine(directory, "data")),
                UserSessionId.Parse("session-diagnostics"),
                ProjectWorkspace.FromLaunchDirectory(directory));
            using var diagnostics = FileDiagnosticLog.OpenSession(resources, "test", TextWriter.Null, TimeProvider.System);
            var progress = new AgentTaskProgress(_broker, _repository, "agent-owner", "call", diagnostics);
            var artifact = AgentTaskParser.ParseArtifact(
                """
                {"schema_version":1,"tasks":[{"name":"secret-name","description":"secret-description","payload":"secret-payload","acceptance_criteria":"secret-criteria"},{"name":"secret-dependent","dependencies":["secret-name"],"description":"secret-description","payload":"secret-payload","acceptance_criteria":"secret-criteria"}]}
                """);
            var handles = progress.Initialize(artifact.Tasks, CancellationToken.None);
            _ = progress.ReplaceChildren(handles[0], AgentTaskPayload.FromTasks(artifact.Tasks), CancellationToken.None);
            _ = progress.ReplaceChildren(handles[0], AgentTaskPayload.FromInstruction("secret-replacement"), CancellationToken.None);
            progress.MarkRunning(handles[0], CancellationToken.None);
            progress.MarkTerminal(handles[0], status, CancellationToken.None);
            if (status == AgentTaskExecutionStatus.Canceled)
            {
                progress.MarkRemainingCanceled(CancellationToken.None);
            }
            else
            {
                progress.MarkBlocked(handles[1], CancellationToken.None);
            }

            var log = await File.ReadAllTextAsync(resources.LogPath);
            _ = await Assert.That(log).Contains("event=\"scheduled\"")
                .And.Contains("event=\"running\"")
                .And.Contains("event=\"terminal\"")
                .And.Contains("outcome=\"superseded\"")
                .And.Contains($"outcome=\"{status.ToString().ToLowerInvariant()}\"")
                .And.Contains("agent=\"agent-owner\"")
                .And.DoesNotContain("secret-");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task Runs_leaf_with_one_payload_attempt(CancellationToken cancellationToken)
    {
        var provider = new AgentTaskQueueProvider([
            "{\"result\":\"contract evidence\",\"verdict\":\"accept\",\"evidence\":\"tests passed\"}",
        ]);
        var runtime = Runtime(provider, cancellationToken);
        await using var registry = runtime.Registry;
        var artifact = AgentTaskParser.ParseArtifact("""
            {"schema_version":1,"tasks":[{"name":"leaf","description":"Implement leaf","payload":"Do leaf work","acceptance_criteria":"Leaf is proven"}]}
            """);

        var result = await new RunnerFixture(runtime, "runner-call", 5, _broker, _repository).Runner
            .Run(artifact, cancellationToken);

        _ = await Assert.That(result.Status).IsEqualTo(AgentTaskExecutionStatus.Succeeded);
        _ = await Assert.That(result.Tasks.Single().AttemptCount).IsEqualTo(1);
        var task = result.Tasks.Single();
        _ = await Assert.That(task.Result).IsEqualTo("contract evidence");
        _ = await Assert.That(task.Execution).IsNull();
        _ = await Assert.That(task.TaskPatch).IsNull();
        _ = await Assert.That(runtime.Sessions.Identities).Count().IsEqualTo(1);
        _ = await Assert.That(runtime.Sessions.Identities.Single().Name).IsEqualTo("leaf");
        _ = await Assert.That(string.Join(",", runtime.Sessions.ProfileIds)).IsEqualTo("agent-task-payload");
        _ = await Assert.That(runtime.Sessions.Identities.All(identity => identity.ParentSessionId == runtime.Parent.SessionId)).IsTrue();
        var retainedChild = runtime.ParentScope.ChildRegistry.ResolveNamedChildScope("leaf");
        _ = await Assert.That(retainedChild.Session.SessionId).IsEqualTo(runtime.Sessions.Identities.Single().SessionId);
        _ = await Assert.That(retainedChild.ChildRegistry.IsAccepting).IsTrue();
        _ = await Assert.That(retainedChild.Session.IsActive()).IsFalse();
        _ = await Assert.That(provider.Requests).Count().IsEqualTo(1);
        _ = await Assert.That(provider.Requests[0].Messages.Count(message => message.Role != LLMRole.System)).IsEqualTo(1);
        var prompt = provider.Requests[0].Messages.Last(message => message.Role == LLMRole.User).Content;
        _ = await Assert.That(prompt).DoesNotContain("Sibling tasks (out of scope):");
        _ = await Assert.That(prompt).Contains("AgentTask role: payload executor");
        _ = await Assert.That(prompt).Contains("Inspect, implement, and verify this instruction:");
        _ = await Assert.That(prompt).Contains("Return only one strict JSON object with no prose or code fence:");
        using var serialized = System.Text.Json.JsonDocument.Parse(result.Serialize());
        var serializedTask = serialized.RootElement.GetProperty("tasks")[0];
        _ = await Assert.That(serializedTask.GetProperty("result").GetString()).IsEqualTo("contract evidence");
        _ = await Assert.That(serializedTask.GetProperty("task_patch").ValueKind).IsEqualTo(System.Text.Json.JsonValueKind.Null);
        _ = await Assert.That(serializedTask.GetProperty("execution").ValueKind).IsEqualTo(System.Text.Json.JsonValueKind.Null);
        _ = await Assert.That(serializedTask.GetProperty("verdict").GetString()).IsEqualTo("accept");
        _ = await Assert.That(serializedTask.GetProperty("evidence").GetString()).IsEqualTo("tests passed");

        var snapshots = ProgressEvents("runner-call");
        _ = await Assert.That(string.Join(',', snapshots.Select(snapshot => snapshot.Revision)))
            .IsEqualTo("1,2,3");
        _ = await Assert.That(snapshots[0].RootNodes.Single().Name).IsEqualTo("leaf");
        _ = await Assert.That(snapshots[0].RootNodes.Single().Description).IsEqualTo("Implement leaf");
        _ = await Assert.That(snapshots[0].RootNodes.Single().Status)
            .IsEqualTo(AgentTaskProgressStatus.Pending);
        _ = await Assert.That(snapshots[1].RootNodes.Single().Status)
            .IsEqualTo(AgentTaskProgressStatus.Running);
        _ = await Assert.That(snapshots[2].RootNodes.Single().Status)
            .IsEqualTo(AgentTaskProgressStatus.Succeeded);
    }

    [Test]
    public async Task Unexpected_graph_failure_leaves_a_fully_terminal_progress_tree()
    {
        var progress = new AgentTaskProgress(_broker, _repository, "owner", "failure-call", TestDiagnosticLog.Instance);
        var artifact = AgentTaskParser.ParseArtifact(
            """
            {"schema_version":1,"tasks":[{"name":"root","description":"Root","payload":[{"name":"running","description":"Running","payload":"work","acceptance_criteria":"Done"},{"name":"pending","dependencies":["running"],"description":"Pending","payload":"work","acceptance_criteria":"Done"}],"acceptance_criteria":"Done"}]}
            """);
        var handles = progress.Initialize(artifact.Tasks, CancellationToken.None);
        progress.MarkRunning(handles[0], CancellationToken.None);

        progress.MarkRemainingFailed(CancellationToken.None);

        var root = progress.CurrentSnapshot().RootNodes.Single();
        _ = await Assert.That(root.Status).IsEqualTo(AgentTaskProgressStatus.Failed);
        _ = await Assert.That(root.Children.All(child => child.Status == AgentTaskProgressStatus.Blocked)).IsTrue();
    }

    [Test]
    public async Task Current_snapshot_is_an_independent_full_tree_with_current_revision()
    {
        var progress = new AgentTaskProgress(_broker, _repository, "owner", "snapshot-call", TestDiagnosticLog.Instance);
        var artifact = AgentTaskParser.ParseArtifact("""
            {"schema_version":1,"tasks":[{"name":"root","description":"Root task","payload":[{"name":"child","description":"Child task","payload":"work","acceptance_criteria":"Done"}],"acceptance_criteria":"Root done"}]}
            """);

        var handles = progress.Initialize(artifact.Tasks, CancellationToken.None);
        var captured = progress.CurrentSnapshot();
        captured.RootNodes[0].Name = "mutated";
        captured.RootNodes[0].Children[0].Status = AgentTaskProgressStatus.Failed;
        captured.RootNodes.Add(new AgentTaskProgressNode { Name = "unexpected" });

        var current = progress.CurrentSnapshot();
        _ = await Assert.That(current.OriginToolCallId).IsEqualTo("snapshot-call");
        _ = await Assert.That(current.Revision).IsEqualTo(1UL);
        _ = await Assert.That(current.RootNodes).HasSingleItem();
        _ = await Assert.That(current.RootNodes[0].Name).IsEqualTo("root");
        _ = await Assert.That(current.RootNodes[0].Children[0].Status)
            .IsEqualTo(AgentTaskProgressStatus.Pending);

        progress.MarkRunning(handles.Single(), CancellationToken.None);
        var running = progress.CurrentSnapshot();
        _ = await Assert.That(running.Revision).IsEqualTo(2UL);
        _ = await Assert.That(running.RootNodes[0].Status)
            .IsEqualTo(AgentTaskProgressStatus.Running);
    }

    [Test]
    public async Task Role_profiles_are_explicit_and_unknown_roles_are_rejected()
    {
        _ = await Assert.That(AgentTaskGraphRunner.ResolveRoleProfile("prepare"))
            .IsEqualTo("agent-task-prepare");
        _ = await Assert.That(AgentTaskGraphRunner.ResolveRoleProfile("execute"))
            .IsEqualTo("agent-task-payload");
        _ = await Assert.That(AgentTaskGraphRunner.ResolveRoleProfile("accept"))
            .IsEqualTo("agent-task-validation");
        _ = await Assert.That(() => AgentTaskGraphRunner.ResolveRoleProfile("worker"))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task Leaf_prompt_describes_the_strict_verdict_contract(CancellationToken cancellationToken)
    {
        var provider = new AgentTaskQueueProvider([
            "{\"result\":\"contract evidence\",\"verdict\":\"accept\",\"evidence\":\"tests passed\"}",
        ]);
        var runtime = Runtime(provider, cancellationToken);
        await using var registry = runtime.Registry;
        var artifact = AgentTaskParser.ParseArtifact("""
            {"schema_version":1,"tasks":[{"name":"leaf","description":"Implement leaf","payload":"Do leaf work","acceptance_criteria":"Leaf is proven"}]}
            """);

        _ = await new RunnerFixture(runtime, "acceptance-contract", 5, _broker, _repository).Runner
            .Run(artifact, cancellationToken);

        var prompt = provider.Requests[0].Messages.Last(message => message.Role == LLMRole.User).Content;
        _ = await Assert.That(prompt).DoesNotContain("Sibling tasks (out of scope):");
        _ = await Assert.That(prompt).Contains("AgentTask role: payload executor");
        _ = await Assert.That(prompt).Contains("Inspect, implement, and verify this instruction:");
        _ = await Assert.That(prompt).Contains("Return only one strict JSON object with no prose or code fence:");
        _ = await Assert.That(prompt).Contains("{\"result\":\"nonblank\",\"verdict\":\"accept\",\"evidence\":\"nonblank\"}");
        _ = await Assert.That(prompt).Contains("{\"result\":\"nonblank\",\"verdict\":\"reject_and_halt\",\"feedback\":\"nonblank\"}");
        _ = await Assert.That(prompt).Contains("{\"result\":\"nonblank\",\"verdict\":\"reject_and_retry\",\"feedback\":\"nonblank\",\"payload\":\"replacement instruction or task array\",\"replacement_result\":\"optional nonblank replacement result\"}");
    }

    [Test]
    public async Task Invalid_leaf_response_fails_without_another_role(CancellationToken cancellationToken)
    {
        var provider = new AgentTaskQueueProvider(["implemented output"]);
        var runtime = Runtime(provider, cancellationToken);
        await using var registry = runtime.Registry;
        var artifact = AgentTaskParser.ParseArtifact("""
            {"schema_version":1,"tasks":[{"name":"leaf","description":"Implement leaf","payload":"Do leaf work","acceptance_criteria":"Leaf is proven"}]}
            """);

        var result = await new RunnerFixture(runtime, "invalid-leaf-response", 5, _broker, _repository).Runner
            .Run(artifact, cancellationToken);

        var task = result.Tasks.Single();
        _ = await Assert.That(task.Status).IsEqualTo(AgentTaskExecutionStatus.Failed);
        _ = await Assert.That(task.AttemptCount).IsEqualTo(1);
        _ = await Assert.That(task.Failure).StartsWith("leaf response invalid:");
        _ = await Assert.That(provider.Requests).Count().IsEqualTo(1);
        _ = await Assert.That(runtime.Sessions.ProfileIds.Single()).IsEqualTo("agent-task-payload");
    }

    [Test]
    public async Task Bounds_leaf_result_and_feedback_in_retry_state(CancellationToken cancellationToken)
    {
        var oversizedResult = new string('c', 20_000);
        var oversizedFeedback = new string('f', 20_000);
        var provider = new AgentTaskQueueProvider([
            string.Concat(
                "{\"result\":\"",
                oversizedResult,
                "\",\"verdict\":\"reject_and_retry\",\"feedback\":\"",
                oversizedFeedback,
                "\",\"payload\":\"second payload\"}"),
            "{\"result\":\"final result\",\"verdict\":\"accept\",\"evidence\":\"done\"}",
        ]);
        var runtime = Runtime(provider, cancellationToken);
        await using var registry = runtime.Registry;
        var artifact = AgentTaskParser.ParseArtifact("""
            {"schema_version":1,"tasks":[{"name":"leaf","description":"Implement leaf","payload":"first payload","acceptance_criteria":"Leaf is proven"}]}
            """);

        var result = await new RunnerFixture(runtime, "bounded-leaf-response", 5, _broker, _repository).Runner
            .Run(artifact, cancellationToken);

        var task = result.Tasks.Single();
        var retainedFeedback = task.RetryFeedback?.Single();
        _ = await Assert.That(retainedFeedback).EndsWith("\n[truncated]");
        _ = await Assert.That(retainedFeedback?.Length).IsEqualTo((16 * 1024) + "\n[truncated]".Length);
        var secondPrompt = provider.Requests[1].Messages.Last(message => message.Role == LLMRole.User).Content;
        _ = await Assert.That(secondPrompt).Contains(new string('c', 16 * 1024));
        _ = await Assert.That(secondPrompt).DoesNotContain(new string('c', (16 * 1024) + 1));
        _ = await Assert.That(secondPrompt).Contains(new string('f', 16 * 1024));
        _ = await Assert.That(secondPrompt).DoesNotContain(new string('f', (16 * 1024) + 1));
    }

    [Test]
    public async Task Halt_rejection_does_not_start_a_second_attempt(CancellationToken cancellationToken)
    {
        var provider = new AgentTaskQueueProvider([
            "{\"result\":\"contract evidence\",\"verdict\":\"reject_and_halt\",\"feedback\":\"not done\"}",
        ]);
        var runtime = Runtime(provider, cancellationToken);
        await using var registry = runtime.Registry;
        var artifact = AgentTaskParser.ParseArtifact("""
            {"schema_version":1,"tasks":[{"name":"leaf","description":"Implement leaf","payload":"Do leaf work","acceptance_criteria":"Leaf is proven"}]}
            """);

        var result = await new RunnerFixture(runtime, "halt-contract", 2, _broker, _repository).Runner
            .Run(artifact, cancellationToken);

        var task = result.Tasks.Single();
        _ = await Assert.That(task.Status).IsEqualTo(AgentTaskExecutionStatus.Failed);
        _ = await Assert.That(task.AttemptCount).IsEqualTo(1);
        _ = await Assert.That(task.Verdict?.Kind).IsEqualTo(AcceptanceVerdictKind.RejectAndHalt);
        _ = await Assert.That(task.Failure).IsEqualTo("not done");
        _ = await Assert.That(provider.Requests).Count().IsEqualTo(1);
    }

    [Test]
    public async Task Retries_payload_five_times_without_repeating_preparation(CancellationToken cancellationToken)
    {
        var provider = new AgentTaskQueueProvider([
            "{\"result\":\"stable preparation\",\"verdict\":\"reject_and_retry\",\"feedback\":\"fix first\",\"payload\":\"second payload\"}",
            "{\"result\":\"stable preparation\",\"verdict\":\"reject_and_retry\",\"feedback\":\"fix second\",\"payload\":\"third payload\"}",
            "{\"result\":\"stable preparation\",\"verdict\":\"reject_and_retry\",\"feedback\":\"still bad\",\"payload\":\"fourth payload\"}",
            "{\"result\":\"stable preparation\",\"verdict\":\"reject_and_retry\",\"feedback\":\"still bad\",\"payload\":\"fifth payload\"}",
            "{\"result\":\"stable preparation\",\"verdict\":\"reject_and_retry\",\"feedback\":\"still bad\",\"payload\":\"sixth payload\"}",
        ]);
        var runtime = Runtime(provider, cancellationToken);
        await using var registry = runtime.Registry;
        var artifact = AgentTaskParser.ParseArtifact("""
            {"schema_version":1,"tasks":[{"name":"retry","description":"Retry task","payload":"first payload","acceptance_criteria":"Must pass"}]}
            """);

        var result = await new RunnerFixture(runtime, "runner-call", 5, _broker, _repository).Runner
            .Run(artifact, cancellationToken);

        var task = result.Tasks.Single();
        _ = await Assert.That(task.Status).IsEqualTo(AgentTaskExecutionStatus.Failed);
        _ = await Assert.That(task.AttemptCount).IsEqualTo(5);
        _ = await Assert.That(task.Result).IsEqualTo("stable preparation");
        _ = await Assert.That(provider.Requests).Count().IsEqualTo(5);
        _ = await Assert.That(provider.Requests.Count(request => request.Messages.Select(message => message.Content).Any(content => content.Contains("AgentTask role: prepare", StringComparison.Ordinal)))).IsEqualTo(0);
        _ = await Assert.That(string.Join(",", provider.Requests.Select(request => request.Messages.Count(message => message.Role != LLMRole.System))))
            .IsEqualTo("1,3,5,7,9");
    }

    [Test]
    public async Task Configured_attempt_budget_limits_each_task_without_repeating_preparation(CancellationToken cancellationToken)
    {
        var provider = new AgentTaskQueueProvider([
            "{\"result\":\"stable preparation\",\"verdict\":\"reject_and_retry\",\"feedback\":\"fix first\",\"payload\":\"second payload\"}",
            "{\"result\":\"stable preparation\",\"verdict\":\"reject_and_retry\",\"feedback\":\"fix second\",\"payload\":\"third payload\"}",
        ]);
        var runtime = Runtime(provider, cancellationToken);
        await using var registry = runtime.Registry;
        var artifact = AgentTaskParser.ParseArtifact("""
            {"schema_version":1,"tasks":[{"name":"retry","description":"Retry task","payload":"first payload","acceptance_criteria":"Must pass"}]}
            """);

        var result = await new RunnerFixture(runtime, "runner-call", 2, _broker, _repository).Runner
            .Run(artifact, cancellationToken);

        var task = result.Tasks.Single();
        _ = await Assert.That(task.Status).IsEqualTo(AgentTaskExecutionStatus.Failed);
        _ = await Assert.That(task.AttemptCount).IsEqualTo(2);
        _ = await Assert.That(provider.Requests).Count().IsEqualTo(2);
        _ = await Assert.That(provider.Requests.Count(request => request.Messages.Select(message => message.Content)
            .Any(content => content.Contains("AgentTask role: prepare", StringComparison.Ordinal)))).IsEqualTo(0);
    }

    [Test]
    public async Task Successful_retry_reuses_leaf_executor_with_ordered_feedback(CancellationToken cancellationToken)
    {
        var provider = new AgentTaskQueueProvider([
            "{\"result\":\"stable preparation\",\"verdict\":\"reject_and_retry\",\"feedback\":\"fix first\",\"payload\":\"second payload\"}",
            "{\"result\":\"stable preparation\",\"verdict\":\"reject_and_retry\",\"feedback\":\"fix second\",\"payload\":\"third payload\"}",
            "{\"result\":\"stable preparation\",\"verdict\":\"accept\",\"evidence\":\"done\"}",
        ]);
        var runtime = Runtime(provider, cancellationToken);
        await using var registry = runtime.Registry;
        var artifact = AgentTaskParser.ParseArtifact("""
            {"schema_version":1,"tasks":[{"name":"retry","description":"Retry task","payload":"first payload","acceptance_criteria":"Must pass"}]}
            """);

        var result = await new RunnerFixture(runtime, "runner-call", 5, _broker, _repository).Runner
            .Run(artifact, cancellationToken);

        var task = result.Tasks.Single();
        _ = await Assert.That(task.Status).IsEqualTo(AgentTaskExecutionStatus.Succeeded);
        _ = await Assert.That(task.AttemptCount).IsEqualTo(3);
        _ = await Assert.That(string.Join(",", task.RetryFeedback ?? [])).IsEqualTo("fix first,fix second");
        _ = await Assert.That(runtime.Sessions.Identities).Count().IsEqualTo(1);
        _ = await Assert.That(runtime.Sessions.ProfileIds.Single()).IsEqualTo("agent-task-payload");
        _ = await Assert.That(string.Join(",", provider.Requests.Select(request => request.Messages.Count(message => message.Role != LLMRole.System))))
            .IsEqualTo("1,3,5");
        using var serialized = System.Text.Json.JsonDocument.Parse(result.Serialize());
        _ = await Assert.That(string.Join(",", serialized.RootElement.GetProperty("tasks")[0]
            .GetProperty("retry_feedback").EnumerateArray().Select(item => item.GetString())))
            .IsEqualTo("fix first,fix second");
    }

    [Test]
    [Arguments(false, false, 3)]
    [Arguments(true, false, 3)]
    [Arguments(false, false, 1)]
    [Arguments(true, false, 1)]
    [Arguments(false, true, 3)]
    [Arguments(true, true, 3)]
    public async Task Retry_notices_report_only_actual_owner_session_attempts(
        bool composite,
        bool invalidReplacement,
        int maximumAttempts,
        CancellationToken cancellationToken)
    {
        const string children = """
            [{"name":"child","description":"Child","payload":"work","acceptance_criteria":"proof"}]
            """;
        const string leafAccept = """
            {"result":"done","verdict":"accept","evidence":"proof"}
            """;
        const string compositeAccept = """
            {"verdict":"accept","evidence":"proof"}
            """;
        var payload = composite ? children : "\"work\"";
        var replacement = invalidReplacement ? "\" \"" : payload;
        var leafResult = composite ? string.Empty : "\"result\":\"secret result\",";
        var rejection = $"{{{leafResult}\"verdict\":\"reject_and_retry\",\"feedback\":\"secret feedback\",\"payload\":{replacement}}}";
        var answers = new List<string>();
        if (composite)
        {
            answers.Add("{\"context\":\"prepared\"}");
        }

        var willRetry = !invalidReplacement && maximumAttempts > 1;
        var actualAttempts = willRetry ? maximumAttempts : 1;
        for (var attempt = 1; attempt <= actualAttempts; attempt++)
        {
            if (composite)
            {
                answers.Add(leafAccept);
            }

            answers.Add(willRetry && attempt == maximumAttempts
                ? composite ? compositeAccept : leafAccept
                : rejection);
        }

        var provider = new AgentTaskQueueProvider([.. answers]);
        var runtime = Runtime(provider, cancellationToken);
        var artifact = AgentTaskParser.ParseArtifact(
            $"{{\"schema_version\":1,\"tasks\":[{{\"name\":\"retry\",\"description\":\"Task\",\"payload\":{payload},\"acceptance_criteria\":\"proof\"}}]}}");

        var result = await new RunnerFixture(runtime, "retry-notices", maximumAttempts, _broker, _repository).Runner
            .Run(artifact, cancellationToken);

        _ = await Assert.That(result.Status).IsEqualTo(willRetry ? AgentTaskExecutionStatus.Succeeded : AgentTaskExecutionStatus.Failed);
        _ = await Assert.That(result.Tasks.Single().AttemptCount).IsEqualTo(actualAttempts);
        var notices = _repository.Replay().Where(published => published.PayloadCase == Event.PayloadOneofCase.RetryNotice).ToArray();
        _ = await Assert.That(notices.Length).IsEqualTo(willRetry ? maximumAttempts - 1 : 0);
        for (var index = 0; index < notices.Length; index++)
        {
            _ = await Assert.That(notices[index].AgentSessionId).IsEqualTo(runtime.Parent.SessionId);
            _ = await Assert.That(notices[index].RetryNotice.Attempt).IsEqualTo(index + 2);
            _ = await Assert.That(notices[index].RetryNotice.RetryAfterMs).IsEqualTo(0);
            _ = await Assert.That(notices[index].RetryNotice.Reason)
                .Contains("task/retry")
                .And.Contains($"attempt {index + 2}/{maximumAttempts}")
                .And.DoesNotContain("secret");
        }
    }

    [Test]
    public async Task Retry_result_replaces_later_prompt_and_final_result(CancellationToken cancellationToken)
    {
        var provider = new AgentTaskQueueProvider([
            "{\"result\":\"initial result\",\"verdict\":\"reject_and_retry\",\"feedback\":\"fix\",\"payload\":\"second payload\",\"replacement_result\":\"replacement result\"}",
            "{\"result\":\"replacement result\",\"verdict\":\"accept\",\"evidence\":\"done\"}",
        ]);
        var runtime = Runtime(provider, cancellationToken);
        await using var registry = runtime.Registry;
        var artifact = AgentTaskParser.ParseArtifact("""
            {"schema_version":1,"tasks":[{"name":"retry","description":"Retry task","payload":"first payload","acceptance_criteria":"Must pass"}]}
            """);

        var result = await new RunnerFixture(runtime, "replacement-context", 5, _broker, _repository).Runner
            .Run(artifact, cancellationToken);

        _ = await Assert.That(result.Tasks.Single().Result).IsEqualTo("replacement result");
        var secondAttempt = provider.Requests[1].Messages.Last(message => message.Role == LLMRole.User).Content;
        _ = await Assert.That(secondAttempt).Contains("replacement result");
        _ = await Assert.That(secondAttempt).DoesNotContain("initial result");
        _ = await Assert.That(provider.Requests).Count().IsEqualTo(2);
    }

    [Test]
    public async Task Retry_without_result_retains_prior_result(CancellationToken cancellationToken)
    {
        var provider = new AgentTaskQueueProvider([
            "{\"result\":\"initial result\",\"verdict\":\"reject_and_retry\",\"feedback\":\"fix\",\"payload\":\"second payload\"}",
            "{\"result\":\"initial result\",\"verdict\":\"accept\",\"evidence\":\"done\"}",
        ]);
        var runtime = Runtime(provider, cancellationToken);
        await using var registry = runtime.Registry;
        var artifact = AgentTaskParser.ParseArtifact("""
            {"schema_version":1,"tasks":[{"name":"retry","description":"Retry task","payload":"first payload","acceptance_criteria":"Must pass"}]}
            """);

        var result = await new RunnerFixture(runtime, "retained-context", 5, _broker, _repository).Runner
            .Run(artifact, cancellationToken);

        _ = await Assert.That(result.Tasks.Single().Result).IsEqualTo("initial result");
        var secondAttempt = provider.Requests[1].Messages.Last(message => message.Role == LLMRole.User).Content;
        _ = await Assert.That(secondAttempt).Contains("initial result");
    }

    [Test]
    public async Task Final_retry_retains_replacement_result_without_further_execution(CancellationToken cancellationToken)
    {
        var provider = new AgentTaskQueueProvider([
            "{\"result\":\"initial result\",\"verdict\":\"reject_and_retry\",\"feedback\":\"fix\",\"payload\":\"replacement\",\"replacement_result\":\"final result\"}",
        ]);
        var runtime = Runtime(provider, cancellationToken);
        await using var registry = runtime.Registry;
        var artifact = AgentTaskParser.ParseArtifact("""
            {"schema_version":1,"tasks":[{"name":"retry","description":"Retry task","payload":"first payload","acceptance_criteria":"Must pass"}]}
            """);

        var result = await new RunnerFixture(runtime, "final-context", 1, _broker, _repository).Runner
            .Run(artifact, cancellationToken);

        _ = await Assert.That(result.Tasks.Single().Status).IsEqualTo(AgentTaskExecutionStatus.Failed);
        _ = await Assert.That(result.Tasks.Single().Result).IsEqualTo("final result");
        _ = await Assert.That(provider.Requests).Count().IsEqualTo(1);
    }

    [Test]
    public async Task Composite_acceptance_receives_failed_child_evidence(CancellationToken cancellationToken)
    {
        var provider = new AgentTaskQueueProvider([
            "{\"context\":\"parent context\"}",
            "{\"result\":\"child context\",\"verdict\":\"reject_and_halt\",\"feedback\":\"child proof failed\"}",
            "{\"verdict\":\"accept\",\"evidence\":\"parent explicitly accepts failure\"}",
        ]);
        var runtime = Runtime(provider, cancellationToken);
        await using var registry = runtime.Registry;
        var artifact = AgentTaskParser.ParseArtifact("""
            {"schema_version":1,"tasks":[{"name":"parent","description":"Parent","payload":[{"name":"child","description":"Child","payload":"child work","acceptance_criteria":"Child proof"}],"acceptance_criteria":"Parent decides"}]}
            """);

        var result = await new RunnerFixture(runtime, "runner-call", 5, _broker, _repository).Runner
            .Run(artifact, cancellationToken);

        var task = result.Tasks.Single();
        _ = await Assert.That(task.Status).IsEqualTo(AgentTaskExecutionStatus.Succeeded);
        _ = await Assert.That(task.Tasks?.Single().Status).IsEqualTo(AgentTaskExecutionStatus.Failed);
        var finalProgress = ProgressEvents("runner-call")[^1].RootNodes.Single();
        _ = await Assert.That(finalProgress.Status).IsEqualTo(AgentTaskProgressStatus.Succeeded);
        _ = await Assert.That(finalProgress.Children.Single().Status).IsEqualTo(AgentTaskProgressStatus.Failed);
        var acceptance = provider.Requests[^1].Messages.Last(message => message.Role == LLMRole.User).Content;
        _ = await Assert.That(acceptance).Contains("Nested task results (structured JSON):");
        _ = await Assert.That(acceptance).Contains("child proof failed");
        _ = await Assert.That(acceptance).Contains("\"status\":\"failed\"");
    }

    [Test]
    public async Task Preparation_patch_replaces_the_effective_progress_subtree(CancellationToken cancellationToken)
    {
        var provider = new AgentTaskQueueProvider([
            "{\"context\":\"parent context\",\"task_patch\":{\"description\":\"Updated parent\",\"payload\":[{\"name\":\"new-child\",\"description\":\"New child\",\"payload\":\"new work\",\"acceptance_criteria\":\"New proof\"}]}}",
            "{\"result\":\"new child context\",\"verdict\":\"accept\",\"evidence\":\"new child done\"}",
            "{\"verdict\":\"accept\",\"evidence\":\"parent done\"}",
        ]);
        var runtime = Runtime(provider, cancellationToken);
        await using var registry = runtime.Registry;
        var artifact = AgentTaskParser.ParseArtifact("""
            {"schema_version":1,"tasks":[{"name":"parent","description":"Parent","payload":[{"name":"old-child","description":"Old child","payload":"old work","acceptance_criteria":"Old proof"}],"acceptance_criteria":"Parent proof"}]}
            """);

        var result = await new RunnerFixture(runtime, "preparation-replacement", 5, _broker, _repository).Runner
            .Run(artifact, cancellationToken);

        _ = await Assert.That(result.Status).IsEqualTo(AgentTaskExecutionStatus.Succeeded);
        var snapshots = ProgressEvents("preparation-replacement");
        var replacement = snapshots.Single(snapshot =>
            snapshot.RootNodes[0].Children.Count == 1
            && snapshot.RootNodes[0].Children[0].Name == "new-child"
            && snapshot.RootNodes[0].Children[0].Status == AgentTaskProgressStatus.Pending);
        _ = await Assert.That(replacement.RootNodes[0].Description).IsEqualTo("Updated parent");
        _ = await Assert.That(replacement.Revision).IsEqualTo(3UL);
        _ = await Assert.That(replacement.RootNodes[0].Children.Select(node => node.Name))
            .DoesNotContain("old-child");
        _ = await Assert.That(snapshots.Any(snapshot =>
            snapshot.RootNodes[0].Description == "Updated parent"
            && snapshot.RootNodes[0].Children.Any(node => node.Name == "old-child"))).IsFalse();
        _ = await Assert.That(snapshots.SkipWhile(snapshot => snapshot.Revision < replacement.Revision)
            .SelectMany(snapshot => snapshot.RootNodes[0].Children)
            .Select(node => node.Name))
            .DoesNotContain("old-child");
        _ = await Assert.That(snapshots[^1].RootNodes[0].Children.Single().Status)
            .IsEqualTo(AgentTaskProgressStatus.Succeeded);
    }

    [Test]
    public async Task Preparation_description_patch_updates_progress_without_replacing_children(CancellationToken cancellationToken)
    {
        var provider = new AgentTaskQueueProvider([
            "{\"context\":\"parent context\",\"task_patch\":{\"description\":\"Updated parent\"}}",
            "{\"result\":\"child result\",\"verdict\":\"accept\",\"evidence\":\"child done\"}",
            "{\"verdict\":\"accept\",\"evidence\":\"parent done\"}",
        ]);
        var runtime = Runtime(provider, cancellationToken);
        await using var registry = runtime.Registry;
        var artifact = AgentTaskParser.ParseArtifact("""
            {"schema_version":1,"tasks":[{"name":"parent","description":"Parent","payload":[{"name":"child","description":"Child","payload":"child work","acceptance_criteria":"Child proof"}],"acceptance_criteria":"Parent proof"}]}
            """);

        _ = await new RunnerFixture(runtime, "description-replacement", 5, _broker, _repository).Runner
            .Run(artifact, cancellationToken);

        var snapshots = ProgressEvents("description-replacement");
        var updated = snapshots.First(snapshot => snapshot.RootNodes[0].Description == "Updated parent");
        var before = snapshots.Single(snapshot => snapshot.Revision == updated.Revision - 1);
        _ = await Assert.That(before.RootNodes[0].Description).IsEqualTo("Parent");
        _ = await Assert.That(updated.RootNodes[0].Children.Single().Name).IsEqualTo("child");
        _ = await Assert.That(snapshots.Where(snapshot => snapshot.Revision >= updated.Revision)
            .All(snapshot => snapshot.RootNodes[0].Description == "Updated parent")).IsTrue();
    }

    [Test]
    public async Task Retry_payload_replaces_stale_progress_descendants(CancellationToken cancellationToken)
    {
        var provider = new AgentTaskQueueProvider([
            "{\"result\":\"parent result\",\"verdict\":\"reject_and_retry\",\"feedback\":\"split it\",\"payload\":[{\"name\":\"retry-child\",\"description\":\"Retry child\",\"payload\":\"child work\",\"acceptance_criteria\":\"Child proof\"}],\"replacement_result\":\"replacement parent result\"}",
            "{\"context\":\"composite preparation context\"}",
            "{\"result\":\"retry child result\",\"verdict\":\"accept\",\"evidence\":\"child done\"}",
            "{\"verdict\":\"accept\",\"evidence\":\"parent done\"}",
        ]);
        var runtime = Runtime(provider, cancellationToken);
        await using var registry = runtime.Registry;
        var artifact = AgentTaskParser.ParseArtifact("""
            {"schema_version":1,"tasks":[{"name":"parent","description":"Parent","payload":"first work","acceptance_criteria":"Parent proof"}]}
            """);

        var result = await new RunnerFixture(runtime, "retry-replacement", 5, _broker, _repository).Runner
            .Run(artifact, cancellationToken);

        _ = await Assert.That(result.Status).IsEqualTo(AgentTaskExecutionStatus.Succeeded);
        _ = await Assert.That(result.Tasks.Single().Context).IsEqualTo("composite preparation context");
        _ = await Assert.That(result.Tasks.Single().Tasks?.Single().Result).IsEqualTo("retry child result");
        _ = await Assert.That(result.Tasks.Single().RetryFeedback?.Single()).IsEqualTo("split it");
        var identities = runtime.Sessions.Identities;
        _ = await Assert.That(identities).Count().IsEqualTo(2);
        var composite = identities.Single(identity => identity.Name == "parent");
        var replacementChild = identities.Single(identity => identity.Name == "retry-child");
        _ = await Assert.That(composite.Name).DoesNotContain("prepare");
        _ = await Assert.That(composite.ParentSessionId).IsEqualTo(runtime.Parent.SessionId);
        _ = await Assert.That(replacementChild.ParentSessionId).IsEqualTo(composite.SessionId);
        _ = await Assert.That(provider.Requests[^1].Messages.Count(message => message.Role != LLMRole.System)).IsEqualTo(5);
        _ = await Assert.That(string.Join(",", runtime.Sessions.ProfileIds)).IsEqualTo("agent-task-payload,agent-task-payload");
        _ = await Assert.That(identities.Select((identity, index) => runtime.Sessions.ProfileIds[index])
            .Count(profile => profile == "agent-task-validation")).IsEqualTo(0);
        var preparationPrompt = provider.Requests[1].Messages.Last(message => message.Role == LLMRole.User).Content;
        _ = await Assert.That(preparationPrompt).Contains("replacement parent result");
        var childExecution = provider.Requests[2].Messages.Last(message => message.Role == LLMRole.User).Content;
        _ = await Assert.That(childExecution).DoesNotContain("replacement parent result");
        _ = await Assert.That(childExecution).DoesNotContain("\n[task/parent] parent result");
        var parentAcceptance = provider.Requests[3].Messages.Last(message => message.Role == LLMRole.User).Content;
        _ = await Assert.That(parentAcceptance).Contains("replacement parent result");
        _ = await Assert.That(provider.Requests[3].Messages.Select(message => message.Content)
            .Any(content => content.Contains("composite preparation context", StringComparison.Ordinal))).IsTrue();
        _ = await Assert.That(provider.Requests[3].Messages.Select(message => message.Content)
            .Any(content => content.Contains("parent done", StringComparison.Ordinal))).IsFalse();
        var snapshots = ProgressEvents("retry-replacement");
        _ = await Assert.That(snapshots.TakeWhile(snapshot => snapshot.RootNodes[0].Children.Count == 0))
            .IsNotEmpty();
        var replacement = snapshots.First(snapshot => snapshot.RootNodes[0].Children.Count > 0);
        _ = await Assert.That(replacement.RootNodes[0].Children.Single().Name).IsEqualTo("retry-child");
        _ = await Assert.That(replacement.RootNodes[0].Children.Single().Status)
            .IsEqualTo(AgentTaskProgressStatus.Pending);
        _ = await Assert.That(snapshots[^1].RootNodes[0].Children.Single().Status)
            .IsEqualTo(AgentTaskProgressStatus.Succeeded);
    }

    [Test]
    public async Task Composite_acceptance_waits_for_an_unrelated_execution(CancellationToken cancellationToken)
    {
        using var provider = new AgentTaskCompositeInterleavingProvider();
        var runtime = Runtime(provider, cancellationToken);
        await using var registry = runtime.Registry;
        var artifact = AgentTaskParser.ParseArtifact("""
            {"schema_version":1,"tasks":[{"name":"parent","description":"Parent","payload":[{"name":"nested","description":"Nested","payload":"nested work","acceptance_criteria":"Nested proof"}],"acceptance_criteria":"Parent proof"}]}
            """);

        var running = new RunnerFixture(runtime, "composite-interleaving", 5, _broker, _repository).Runner
            .Run(artifact, cancellationToken);
        await provider.WaitForRequest(cancellationToken);
        await provider.WaitForRequest(cancellationToken);
        var composite = runtime.Sessions.ResolveScope("parent").Session;
        _ = await composite.SendTextMessage("unrelated message", cancellationToken);
        await provider.WaitForRequest(cancellationToken);

        provider.FinishNested();
        _ = await runtime.Sessions.ResolveScope("nested").Session.Wait(0, cancellationToken);
        _ = await Assert.That(provider.Requests).Count().IsEqualTo(3);
        _ = await Assert.That(running.IsCompleted).IsFalse();

        provider.FinishUnrelated();
        await provider.WaitForRequest(cancellationToken);
        var result = await running;

        _ = await Assert.That(result.Status).IsEqualTo(AgentTaskExecutionStatus.Succeeded);
        _ = await Assert.That(result.Tasks.Single().Tasks?.Single().Result).IsEqualTo("nested result");
        _ = await Assert.That(result.Tasks.Single().Verdict?.Evidence).IsEqualTo("parent proof");
        _ = await Assert.That(provider.Requests).Count().IsEqualTo(4);
        var unrelatedRequest = provider.Requests[2];
        _ = await Assert.That(unrelatedRequest.Messages.Last(message => message.Role == LLMRole.User).Content)
            .IsEqualTo("unrelated message");
        var acceptanceRequest = provider.Requests[3];
        _ = await Assert.That(acceptanceRequest.Messages.Last(message => message.Role == LLMRole.User).Content)
            .Contains("AgentTask role: acceptance reviewer");
        _ = await Assert.That(acceptanceRequest.Messages.Select(message => message.Content))
            .Contains("unrelated answer");
        _ = await Assert.That(runtime.Sessions.Identities).Count().IsEqualTo(2);
        var identities = runtime.Sessions.Identities;
        _ = await Assert.That(identities.Single(identity => identity.Name == "parent").ParentSessionId)
            .IsEqualTo(runtime.Parent.SessionId);
        _ = await Assert.That(identities.Single(identity => identity.Name == "nested").ParentSessionId)
            .IsEqualTo(composite.SessionId);
        _ = await Assert.That(string.Join(",", runtime.Sessions.ProfileIds))
            .IsEqualTo("agent-task-prepare,agent-task-payload");
    }

    [Test]
    public async Task Recursive_composites_retain_role_history_and_parentage(CancellationToken cancellationToken)
    {
        var provider = new AgentTaskQueueProvider([
            "{\"context\":\"top preparation\"}",
            "{\"context\":\"child preparation\"}",
            "{\"result\":\"leaf result\",\"verdict\":\"accept\",\"evidence\":\"leaf done\"}",
            "{\"verdict\":\"accept\",\"evidence\":\"child done\"}",
            "{\"verdict\":\"accept\",\"evidence\":\"top done\"}",
        ]);
        var runtime = Runtime(provider, cancellationToken);
        await using var registry = runtime.Registry;
        var artifact = AgentTaskParser.ParseArtifact("""
            {"schema_version":1,"tasks":[{"name":"top","description":"Top","payload":[{"name":"child","description":"Child","payload":[{"name":"leaf","description":"Leaf","payload":"leaf work","acceptance_criteria":"Leaf done"}],"acceptance_criteria":"Child done"}],"acceptance_criteria":"Top done"}]}
            """);

        var result = await new RunnerFixture(runtime, "recursive-composites", 5, _broker, _repository).Runner
            .Run(artifact, cancellationToken);

        _ = await Assert.That(result.Status).IsEqualTo(AgentTaskExecutionStatus.Succeeded);
        var identities = runtime.Sessions.Identities;
        _ = await Assert.That(identities).Count().IsEqualTo(3);
        var topComposite = identities.Single(identity => identity.Name == "top");
        var childComposite = identities.Single(identity => identity.Name == "child");
        var leaf = identities.Single(identity => identity.Name == "leaf");
        _ = await Assert.That(topComposite.ParentSessionId).IsEqualTo(runtime.Parent.SessionId);
        _ = await Assert.That(childComposite.ParentSessionId).IsEqualTo(topComposite.SessionId);
        _ = await Assert.That(leaf.ParentSessionId).IsEqualTo(childComposite.SessionId);
        _ = await Assert.That(provider.Requests[3].Messages.Count(message => message.Role != LLMRole.System)).IsEqualTo(5);
        _ = await Assert.That(provider.Requests[3].Messages.Select(message => message.Content)
            .Any(content => content.Contains("child preparation", StringComparison.Ordinal))).IsTrue();
        _ = await Assert.That(provider.Requests[4].Messages.Count(message => message.Role != LLMRole.System)).IsEqualTo(3);
        _ = await Assert.That(provider.Requests[4].Messages.Select(message => message.Content)
            .Any(content => content.Contains("top preparation", StringComparison.Ordinal))).IsTrue();
        _ = await Assert.That(provider.Requests[4].Messages.Select(message => message.Content)
            .Any(content => content.Contains("top done", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task Accepted_leaf_result_is_used_as_dependency_summary(CancellationToken cancellationToken)
    {
        var provider = new AgentTaskQueueProvider([
            "{\"context\":\"root preparation\"}",
            "{\"result\":\"dependency result\",\"verdict\":\"accept\",\"evidence\":\"dependency proof\"}",
            "{\"result\":\"dependent context\",\"verdict\":\"accept\",\"evidence\":\"dependent proof\"}",
            "{\"verdict\":\"accept\",\"evidence\":\"root proof\"}",
        ]);
        var runtime = Runtime(provider, cancellationToken);
        await using var registry = runtime.Registry;
        var artifact = AgentTaskParser.ParseArtifact("""
            {"schema_version":1,"tasks":[
              {"name":"root","description":"Root","payload":[
                {"name":"prerequisite","description":"Prerequisite","payload":"work","acceptance_criteria":"Done"},
                {"name":"dependent","dependencies":["prerequisite"],"description":"Dependent","payload":"dependent work","acceptance_criteria":"Done"}
              ],"acceptance_criteria":"Root done"}
            ]}
            """);

        var result = await new RunnerFixture(runtime, "dependency-evidence", 5, _broker, _repository).Runner
            .Run(artifact, cancellationToken);

        _ = await Assert.That(result.Status).IsEqualTo(AgentTaskExecutionStatus.Succeeded);
        var dependentPrompt = provider.Requests[2].Messages.Last(message => message.Role == LLMRole.User).Content;
        _ = await Assert.That(dependentPrompt).Contains("[prerequisite] dependency result");
    }

    [Test]
    public async Task Composite_result_reaches_only_its_direct_dependent_with_structured_mixed_child_results(
        CancellationToken cancellationToken)
    {
        var provider = new AgentTaskQueueProvider([
            "{\"context\":\"root preparation\"}",
            "{\"context\":\"unrelated preparation\"}",
            "{\"result\":\"cousin result\",\"verdict\":\"accept\",\"evidence\":\"cousin evidence\"}",
            "{\"verdict\":\"accept\",\"evidence\":\"unrelated evidence\"}",
            "{\"context\":\"parent preparation\"}",
            "{\"result\":\"successful child result\",\"verdict\":\"accept\",\"evidence\":\"successful child evidence\"}",
            "{\"result\":\"failed child result\",\"verdict\":\"reject_and_halt\",\"feedback\":\"failed child feedback\"}",
            "{\"verdict\":\"accept\",\"evidence\":\"parent acceptance evidence\"}",
            "{\"result\":\"dependent result\",\"verdict\":\"accept\",\"evidence\":\"dependent evidence\"}",
            "{\"verdict\":\"accept\",\"evidence\":\"root acceptance evidence\"}",
        ]);
        var runtime = Runtime(provider, cancellationToken);
        await using var registry = runtime.Registry;
        var artifact = AgentTaskParser.ParseArtifact("""
            {"schema_version":1,"tasks":[
              {"name":"root","description":"Root","payload":[
                {"name":"unrelated","description":"Unrelated","payload":[
                  {"name":"cousin","description":"Cousin","payload":"cousin work","acceptance_criteria":"Cousin done"}
                ],"acceptance_criteria":"Unrelated done"},
                {"name":"parent","dependencies":["unrelated"],"description":"Parent","payload":[
                  {"name":"successful-child","description":"Successful child","payload":"successful work","acceptance_criteria":"Successful done"},
                  {"name":"failed-child","dependencies":["successful-child"],"description":"Failed child","payload":"failed work","acceptance_criteria":"Failed done"}
                ],"acceptance_criteria":"Parent decides"},
                {"name":"dependent","dependencies":["parent"],"description":"Dependent","payload":"dependent work","acceptance_criteria":"Dependent done"}
              ],"acceptance_criteria":"Root decides"}
            ]}
            """);

        var graphResult = await new RunnerFixture(runtime, "composite-dependency-result", 5, _broker, _repository).Runner
            .Run(artifact, cancellationToken);

        _ = await Assert.That(graphResult.Status).IsEqualTo(AgentTaskExecutionStatus.Succeeded);
        _ = await Assert.That(graphResult.Tasks).HasSingleItem();
        var rootTasks = graphResult.Tasks[0].Tasks
            ?? throw new InvalidOperationException("Root composite tasks were not produced.");
        var parentResult = rootTasks[1].Result
            ?? throw new InvalidOperationException("Composite result was not produced.");
        _ = await Assert.That(parentResult).Contains("\"name\":\"successful-child\"");
        _ = await Assert.That(parentResult).Contains("\"result\":\"successful child result\"");
        _ = await Assert.That(parentResult).Contains("\"name\":\"failed-child\"");
        _ = await Assert.That(parentResult).Contains("\"result\":\"failed child result\"");
        _ = await Assert.That(parentResult).Contains("\"status\":\"failed\"");

        var parentAcceptancePrompt = provider.Requests[7].Messages.Last(message => message.Role == LLMRole.User).Content;
        _ = await Assert.That(parentAcceptancePrompt).Contains("Nested task results (structured JSON):");
        _ = await Assert.That(parentAcceptancePrompt).Contains("\"result\":\"successful child result\"");
        _ = await Assert.That(parentAcceptancePrompt).Contains("\"result\":\"failed child result\"");

        var dependentPrompt = provider.Requests[8].Messages.Last(message => message.Role == LLMRole.User).Content;
        _ = await Assert.That(dependentPrompt).Contains($"[parent] {parentResult}");
        _ = await Assert.That(dependentPrompt).DoesNotContain("parent acceptance evidence");
        _ = await Assert.That(dependentPrompt).DoesNotContain("unrelated evidence");
        _ = await Assert.That(dependentPrompt).DoesNotContain("cousin result");
        _ = await Assert.That(dependentPrompt).DoesNotContain("cousin evidence");
    }

    [Test]
    public async Task Failed_dependency_blocks_the_complete_pending_subtree(CancellationToken cancellationToken)
    {
        var provider = new AgentTaskQueueProvider([
            "{\"context\":\"root preparation\"}",
            "{\"result\":\"prerequisite context\",\"verdict\":\"reject_and_halt\",\"feedback\":\"not done\"}",
            "{\"verdict\":\"reject_and_halt\",\"feedback\":\"root cannot proceed\"}",
        ]);
        var runtime = Runtime(provider, cancellationToken);
        await using var registry = runtime.Registry;
        var artifact = AgentTaskParser.ParseArtifact("""
            {"schema_version":1,"tasks":[
              {"name":"root","description":"Root","payload":[
                {"name":"prerequisite","description":"Prerequisite","payload":"work","acceptance_criteria":"Done"},
                {"name":"dependent","dependencies":["prerequisite"],"description":"Dependent","payload":[{"name":"nested","description":"Nested","payload":"nested work","acceptance_criteria":"Nested done"}],"acceptance_criteria":"Dependent done"}
              ],"acceptance_criteria":"Root done"}
            ]}
            """);

        var result = await new RunnerFixture(runtime, "blocked-subtree", 5, _broker, _repository).Runner
            .Run(artifact, cancellationToken);

        _ = await Assert.That(result.Status).IsEqualTo(AgentTaskExecutionStatus.Failed);
        _ = await Assert.That(provider.Requests).Count().IsEqualTo(3);
        var final = ProgressEvents("blocked-subtree")[^1];
        _ = await Assert.That(final.RootNodes).HasSingleItem();
        _ = await Assert.That(final.RootNodes[0].Status).IsEqualTo(AgentTaskProgressStatus.Failed);
        _ = await Assert.That(final.RootNodes[0].Children[0].Status).IsEqualTo(AgentTaskProgressStatus.Failed);
        _ = await Assert.That(final.RootNodes[0].Children[1].Status).IsEqualTo(AgentTaskProgressStatus.Blocked);
        _ = await Assert.That(final.RootNodes[0].Children[1].Children.Single().Status)
            .IsEqualTo(AgentTaskProgressStatus.Blocked);
    }

    [Test]
    public async Task Schedules_ready_tasks_concurrently_and_releases_dependents_immediately(
        CancellationToken cancellationToken)
    {
        var provider = new AgentTaskSchedulingProvider();
        var runtime = Runtime(provider, cancellationToken);
        await using var registry = runtime.Registry;
        var artifact = AgentTaskParser.ParseArtifact("""
            {"schema_version":1,"tasks":[
              {"name":"fast","description":"Fast","payload":"fast work","acceptance_criteria":"Done"},
              {"name":"slow","description":"Slow","payload":"slow work","acceptance_criteria":"Done"},
              {"name":"dependent","dependencies":["fast"],"description":"Dependent","payload":"dependent work","acceptance_criteria":"Done"}
            ]}
            """);

        var result = await new RunnerFixture(runtime, "runner-call", 5, _broker, _repository).Runner
            .Run(artifact, cancellationToken);

        _ = await Assert.That(result.Status).IsEqualTo(AgentTaskExecutionStatus.Succeeded);
        _ = await Assert.That(provider.MaximumActive >= 2).IsTrue();
        _ = await Assert.That(provider.DependentStartedBeforeSlowFinished).IsTrue();
        _ = await Assert.That(string.Join(",", result.Tasks.Select(task => task.Name))).IsEqualTo("fast,slow,dependent");
        var snapshots = ProgressEvents("runner-call");
        _ = await Assert.That(snapshots.All(snapshot =>
            string.Join(',', snapshot.RootNodes.Select(node => node.Name)) == "fast,slow,dependent"))
            .IsTrue();
        var fastSucceeded = snapshots.ToList().FindIndex(snapshot =>
            snapshot.RootNodes[0].Status == AgentTaskProgressStatus.Succeeded);
        var dependentRunning = snapshots.ToList().FindIndex(snapshot =>
            snapshot.RootNodes[2].Status == AgentTaskProgressStatus.Running);
        _ = await Assert.That(dependentRunning > fastSucceeded).IsTrue();
    }

    [Test]
    public async Task Failed_role_reports_failure_and_retains_inactive_child(CancellationToken cancellationToken)
    {
        var provider = new TerminalFailureProvider("role failed");
        var runtime = Runtime(provider, cancellationToken);
        await using var registry = runtime.Registry;
        var artifact = AgentTaskParser.ParseArtifact("""
            {"schema_version":1,"tasks":[{"name":"failed-role","description":"Fail task","payload":"work","acceptance_criteria":"Done"}]}
            """);

        var result = await new RunnerFixture(runtime, "failed-role", 5, _broker, _repository).Runner
            .Run(artifact, cancellationToken);

        var task = result.Tasks.Single();
        _ = await Assert.That(result.Status).IsEqualTo(AgentTaskExecutionStatus.Failed);
        _ = await Assert.That(task.Status).IsEqualTo(AgentTaskExecutionStatus.Failed);
        _ = await Assert.That(task.Failure).IsEqualTo("execution agent failed: role failed");
        var retainedChild = runtime.ParentScope.ChildRegistry.ResolveNamedChildScope("failed-role");
        _ = await Assert.That(retainedChild.Session.SessionId).IsEqualTo(runtime.Sessions.Identities.Single().SessionId);
        _ = await Assert.That(retainedChild.ChildRegistry.IsAccepting).IsTrue();
        _ = await Assert.That(retainedChild.Session.IsActive()).IsFalse();
    }

    [Test]
    public async Task Nested_graph_retains_the_created_scope_tree_until_owner_shutdown(CancellationToken cancellationToken)
    {
        var provider = new AgentTaskQueueProvider([
            "{\"context\":\"prepared\"}",
            "{\"result\":\"child evidence\",\"verdict\":\"accept\",\"evidence\":\"done\"}",
            "{\"verdict\":\"accept\",\"evidence\":\"validated\"}",
        ]);
        var runtime = Runtime(provider, cancellationToken);
        await using var registry = runtime.Registry;
        var artifact = AgentTaskParser.ParseArtifact("""
            {"schema_version":1,"tasks":[{"name":"root","description":"Root task","payload":[{"name":"child","description":"Child task","payload":"work","acceptance_criteria":"Child done"}],"acceptance_criteria":"Root done"}]}
            """);

        var result = await new RunnerFixture(runtime, "nested-cleanup", 5, _broker, _repository).Runner
            .Run(artifact, cancellationToken);

        _ = await Assert.That(result.Status).IsEqualTo(AgentTaskExecutionStatus.Succeeded);
        _ = await Assert.That(runtime.Sessions.Identities).Count().IsEqualTo(2);
        var compositeScope = runtime.ParentScope.ChildRegistry.ResolveNamedChildScope("root");
        var nestedScope = compositeScope.ChildRegistry.ResolveNamedChildScope("child");
        _ = await Assert.That(compositeScope.Session.SessionId).IsEqualTo(runtime.Sessions.ResolveScope("root").Session.SessionId);
        _ = await Assert.That(nestedScope.Session.SessionId).IsEqualTo(runtime.Sessions.ResolveScope("child").Session.SessionId);
        _ = await Assert.That(nestedScope.Session.ParentSessionId).IsEqualTo(compositeScope.Session.SessionId);
        _ = await Assert.That(runtime.ParentScope.ChildRegistry.SnapshotDescendants()).Count().IsEqualTo(2);
        _ = await Assert.That(runtime.ParentScope.ChildRegistry.SnapshotDescendants().Where(session => session.IsActive())).IsEmpty();
        _ = await Assert.That(compositeScope.ChildRegistry.IsAccepting).IsTrue();
        _ = await Assert.That(nestedScope.ChildRegistry.IsAccepting).IsTrue();

        await registry.BeginShutdown();

        _ = await Assert.That(runtime.ParentScope.ChildRegistry.SnapshotDescendants()).IsEmpty();
        _ = await Assert.That(compositeScope.ChildRegistry.IsAccepting).IsFalse();
        _ = await Assert.That(nestedScope.ChildRegistry.IsAccepting).IsFalse();
    }

    [Test]
    public async Task Cancellation_interrupts_and_joins_active_internal_child(CancellationToken cancellationToken)
    {
        using var provider = new AgentTaskBlockingProvider();
        var runtime = Runtime(provider, cancellationToken);
        await using var registry = runtime.Registry;
        var artifact = AgentTaskParser.ParseArtifact("""
            {"schema_version":1,"tasks":[{"name":"cancel","description":"Cancel task","payload":"work","acceptance_criteria":"Done"}]}
            """);
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var running = new RunnerFixture(runtime, "runner-call", 5, _broker, _repository).Runner
            .Run(artifact, canceled.Token);
        await provider.WaitUntilArrived(cancellationToken);

        await canceled.CancelAsync();
        _ = await Assert.That(running).Throws<OperationCanceledException>();

        var retainedChild = runtime.ParentScope.ChildRegistry.ResolveNamedChildScope("cancel");
        _ = await Assert.That(retainedChild.Session.SessionId).IsEqualTo(runtime.Sessions.Identities.Single().SessionId);
        _ = await Assert.That(retainedChild.ChildRegistry.IsAccepting).IsTrue();
        _ = await Assert.That(retainedChild.Session.IsActive()).IsFalse();
        var snapshots = ProgressEvents("runner-call");
        _ = await Assert.That(snapshots[^1].RootNodes.Single().Status)
            .IsEqualTo(AgentTaskProgressStatus.Canceled);
        _ = await Assert.That(snapshots[^1].Revision).IsEqualTo((ulong)snapshots.Length);
        _ = await Assert.That(_repository.Replay()[^1].AgentTaskProgressSnapshot.Revision)
            .IsEqualTo(snapshots[^1].Revision);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Repeated_graphs_reuse_leaf_and_nested_history(bool nested, CancellationToken cancellationToken)
    {
        string[] answers = nested
            ? ["{\"context\":\"first preparation\",\"task_patch\":{\"model\":\"missing-provider/missing-model\"}}", "{\"result\":\"first result\",\"verdict\":\"accept\",\"evidence\":\"done\"}", "{\"verdict\":\"accept\",\"evidence\":\"first validation\"}",
                "{\"context\":\"second preparation\"}", "{\"result\":\"second result\",\"verdict\":\"accept\",\"evidence\":\"done\"}", "{\"verdict\":\"accept\",\"evidence\":\"second validation\"}"]
            : ["{\"result\":\"first result\",\"verdict\":\"accept\",\"evidence\":\"done\"}", "{\"result\":\"second result\",\"verdict\":\"accept\",\"evidence\":\"done\"}"];
        var provider = new AgentTaskQueueProvider(answers);
        var runtime = Runtime(provider, cancellationToken);
        await using var registry = runtime.Registry;
        var artifact = AgentTaskParser.ParseArtifact(nested
            ? """
                {"schema_version":1,"tasks":[{"name":"root","description":"Root","payload":[{"name":"leaf","description":"Leaf","payload":"work","acceptance_criteria":"Done"}],"acceptance_criteria":"Done"}]}
                """
            : """
                {"schema_version":1,"tasks":[{"name":"leaf","description":"Leaf","payload":"work","acceptance_criteria":"Done"}]}
                """);

        var first = await new RunnerFixture(runtime, "first-graph", 5, _broker, _repository).Runner.Run(artifact, cancellationToken);
        var identities = runtime.Sessions.Identities.Select(identity => identity.SessionId).ToArray();
        var firstLeafRequest = provider.Requests[nested ? 1 : 0];
        var second = await new RunnerFixture(runtime, "second-graph", 5, _broker, _repository).Runner.Run(artifact, cancellationToken);
        var secondLeafRequest = provider.Requests[nested ? 4 : 1];

        _ = await Assert.That(first.Status).IsEqualTo(AgentTaskExecutionStatus.Succeeded);
        _ = await Assert.That(second.Status).IsEqualTo(AgentTaskExecutionStatus.Succeeded);
        _ = await Assert.That(nested ? second.Tasks.Single().Tasks?.Single().Result : second.Tasks.Single().Result).IsEqualTo("second result");
        _ = await Assert.That(string.Join(',', runtime.Sessions.Identities.Select(identity => identity.SessionId)))
            .IsEqualTo(string.Join(',', identities));
        _ = await Assert.That(secondLeafRequest.Messages.Count(message => message.Role != LLMRole.System))
            .IsEqualTo(firstLeafRequest.Messages.Count(message => message.Role != LLMRole.System) + 2);
        _ = await Assert.That(secondLeafRequest.Messages.Select(message => message.Content)).Contains(answers[nested ? 1 : 0]);
        var leafOwner = nested ? runtime.ParentScope.ChildRegistry.ResolveNamedChildScope("root") : runtime.ParentScope;
        var leafScope = leafOwner.ChildRegistry.ResolveNamedChildScope("leaf");
        _ = await Assert.That(leafScope.Session.ParentSessionId).IsEqualTo(leafOwner.Session.SessionId);
        _ = await Assert.That(runtime.ParentScope.ChildRegistry.SnapshotDescendants().Any(session => session.IsActive())).IsFalse();
        if (nested)
        {
            _ = await Assert.That(provider.Requests[3].Messages.Select(message => message.Content)).Contains(answers[2]);
        }

        await registry.BeginShutdown();
        _ = await Assert.That(leafScope.ChildRegistry.IsAccepting).IsFalse();
        _ = await Assert.That(runtime.ParentScope.ChildRegistry.SnapshotDescendants()).IsEmpty();
    }

    [Test]
    public async Task Reuses_manual_agent_configuration_and_delivery_before_resolving_requested_model(CancellationToken cancellationToken)
    {
        var provider = new AgentTaskQueueProvider([
            "manual history",
            "parent acknowledged manual completion",
            "{\"result\":\"task result\",\"verdict\":\"accept\",\"evidence\":\"done\"}",
            "parent acknowledged task completion",
        ]);
        var runtime = Runtime(provider, cancellationToken);
        await using var registry = runtime.Registry;
        var manualScope = runtime.ParentScope.AgentSpawner.SpawnScope(new AgentLaunchRequest(
            runtime.Parent,
            runtime.Selection,
            "worker",
            runtime.Selection.RequestedModel,
            "Existing Agent",
            "manual scope",
            HistoryForkSelection.Parse(string.Empty),
            new HistoryForkBoundary.AfterCompletedHistory(),
            AgentCompletionDeliveryPolicy.Automatic));
        _ = await manualScope.Session.SendAndWaitForResult("manual prompt", cancellationToken);
        await runtime.Parent.Settled();
        var originalSelection = manualScope.Session.CurrentSelection();
        var artifact = AgentTaskParser.ParseArtifact("""
            {"schema_version":1,"tasks":[{"name":"Existing Agent","description":"Reuse manual child","model":"missing-provider/missing-model","payload":"work","acceptance_criteria":"Done"}]}
            """);

        var result = await new RunnerFixture(runtime, "manual-reuse", 5, _broker, _repository).Runner.Run(artifact, cancellationToken);
        await runtime.Parent.Settled();

        _ = await Assert.That(result.Tasks.Single().Result).IsEqualTo("task result");
        _ = await Assert.That(runtime.Sessions.Identities).Count().IsEqualTo(1);
        _ = await Assert.That(runtime.Sessions.ProfileIds.Single()).IsEqualTo("worker");
        _ = await Assert.That(manualScope.Session.CurrentSelection()).IsEqualTo(originalSelection);
        _ = await Assert.That(manualScope.ParentScope.DeliveryPolicy).IsEqualTo(AgentCompletionDeliveryPolicy.Automatic);
        _ = await Assert.That(provider.Requests[2].Messages.Select(message => message.Content)).Contains("manual history");
        _ = await Assert.That(_repository.Replay().Any(published => published.AgentSessionId == runtime.Parent.SessionId
            && published.InputAdmitted is not null && published.InputAdmitted.Content.Contains("task result", StringComparison.Ordinal))).IsTrue();

        var newAgentArtifact = AgentTaskParser.ParseArtifact("""
            {"schema_version":1,"tasks":[{"name":"new-agent","description":"Cannot launch","model":"missing-provider/missing-model","payload":"work","acceptance_criteria":"Done"}]}
            """);
        var failed = await new RunnerFixture(runtime, "invalid-new-model", 5, _broker, _repository).Runner.Run(newAgentArtifact, cancellationToken);
        _ = await Assert.That(failed.Status).IsEqualTo(AgentTaskExecutionStatus.Failed);
        _ = await Assert.That(runtime.Sessions.Identities).Count().IsEqualTo(1);
        _ = await Assert.That(provider.Requests).Count().IsEqualTo(4);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Overlapping_graphs_skip_canceled_reservations_without_overtaking_busy_agent(bool cancelTail, CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider(
            LLMEvent.Completed("stop", 1, 0, 1, "unrelated result", []),
            LLMEvent.Completed("stop", 1, 0, 1, "{\"result\":\"first result\",\"verdict\":\"accept\",\"evidence\":\"done\"}", []),
            LLMEvent.Completed("stop", 1, 0, 1, "{\"result\":\"last result\",\"verdict\":\"accept\",\"evidence\":\"done\"}", []));
        var runtime = Runtime(provider, cancellationToken);
        await using var registry = runtime.Registry;
        var manualScope = runtime.ParentScope.AgentSpawner.SpawnScope(new AgentLaunchRequest(
            runtime.Parent,
            runtime.Selection,
            "worker",
            runtime.Selection.RequestedModel,
            "shared",
            string.Empty,
            HistoryForkSelection.Parse(string.Empty),
            new HistoryForkBoundary.AfterCompletedHistory(),
            AgentCompletionDeliveryPolicy.RetainedOnly));
        var predecessor = manualScope.Session.SendAndWaitForResult("unrelated prompt", cancellationToken);
        await provider.Arrived(cancellationToken);
        var firstArtifact = AgentTaskParser.ParseArtifact("""
            {"schema_version":1,"tasks":[{"name":"shared","description":"First","payload":"first work","acceptance_criteria":"Done"}]}
            """);
        var canceledArtifact = AgentTaskParser.ParseArtifact("""
            {"schema_version":1,"tasks":[{"name":"shared","description":"Canceled","payload":"canceled work","acceptance_criteria":"Done"}]}
            """);
        var lastArtifact = AgentTaskParser.ParseArtifact("""
            {"schema_version":1,"tasks":[{"name":"shared","description":"Last","payload":"last work","acceptance_criteria":"Done"}]}
            """);
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var first = new RunnerFixture(runtime, "first-shared", 5, _broker, _repository).Runner.Run(firstArtifact, cancellationToken);
        var canceledGraph = new RunnerFixture(runtime, "canceled-shared", 5, _broker, _repository).Runner.Run(canceledArtifact, canceled.Token);
        Task<AgentTaskGraphResult>? last = null;
        if (!cancelTail)
        {
            last = new RunnerFixture(runtime, "last-shared", 5, _broker, _repository).Runner.Run(lastArtifact, cancellationToken);
        }

        await canceled.CancelAsync();
        _ = await Assert.That(canceledGraph).Throws<OperationCanceledException>();
        last ??= new RunnerFixture(runtime, "last-shared", 5, _broker, _repository).Runner.Run(lastArtifact, cancellationToken);
        _ = await Assert.That(predecessor.IsCompleted).IsFalse();
        _ = await Assert.That(provider.Requests).Count().IsEqualTo(1);
        _ = await Assert.That(runtime.Sessions.Identities).Count().IsEqualTo(1);

        provider.Release();
        _ = await Assert.That(await predecessor).IsEqualTo("unrelated result");
        await provider.Arrived(cancellationToken);
        _ = await Assert.That(provider.Requests[1].Messages.Last(message => message.Role == LLMRole.User).Content).Contains("first work");
        _ = await Assert.That(last.IsCompleted).IsFalse();
        provider.Release();
        _ = await Assert.That((await first).Tasks.Single().Result).IsEqualTo("first result");
        await provider.Arrived(cancellationToken);
        _ = await Assert.That(provider.Requests[2].Messages.Last(message => message.Role == LLMRole.User).Content).Contains("last work");
        provider.Release();
        _ = await Assert.That((await last).Tasks.Single().Result).IsEqualTo("last result");
        _ = await Assert.That(provider.Requests).Count().IsEqualTo(3);
        _ = await Assert.That(string.Join(' ', provider.Requests.SelectMany(request => request.Messages).Select(message => message.Content)))
            .DoesNotContain("canceled work");
        _ = await Assert.That(_repository.Replay().Any(published => published.InputAdmitted is not null
            && published.InputAdmitted.Content.Contains("canceled work", StringComparison.Ordinal))).IsFalse();
        _ = await Assert.That(runtime.ParentScope.ChildRegistry.ResolveNamedChildScope("shared").Session.SessionId).IsEqualTo(manualScope.Session.SessionId);
        _ = await Assert.That(manualScope.Session.IsActive()).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Reuses_agent_after_failed_or_canceled_graph(bool cancelFirst, CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider(
            LLMEvent.Completed("stop", 1, 0, 1, "invalid leaf verdict", []),
            LLMEvent.Completed("stop", 1, 0, 1, "{\"result\":\"recovered\",\"verdict\":\"accept\",\"evidence\":\"done\"}", []));
        var runtime = Runtime(provider, cancellationToken);
        await using var registry = runtime.Registry;
        var artifact = AgentTaskParser.ParseArtifact("""
            {"schema_version":1,"tasks":[{"name":"recover","description":"Recover","payload":"work","acceptance_criteria":"Done"}]}
            """);
        using var firstCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var first = new RunnerFixture(runtime, "failed-predecessor", 5, _broker, _repository).Runner.Run(artifact, firstCancellation.Token);
        await provider.Arrived(cancellationToken);
        var retainedScope = runtime.ParentScope.ChildRegistry.ResolveNamedChildScope("recover");
        var second = new RunnerFixture(runtime, "recovered-graph", 5, _broker, _repository).Runner.Run(artifact, cancellationToken);
        if (cancelFirst)
        {
            await firstCancellation.CancelAsync();
            _ = await Assert.That(first).Throws<OperationCanceledException>();
        }
        else
        {
            provider.Release();
            _ = await Assert.That((await first).Status).IsEqualTo(AgentTaskExecutionStatus.Failed);
        }

        await provider.Arrived(cancellationToken);
        _ = await Assert.That(second.IsCompleted).IsFalse();
        provider.Release();
        _ = await Assert.That((await second).Tasks.Single().Result).IsEqualTo("recovered");
        _ = await Assert.That(runtime.Sessions.Identities).Count().IsEqualTo(1);
        _ = await Assert.That(runtime.ParentScope.ChildRegistry.ResolveNamedChildScope("recover").Session.SessionId)
            .IsEqualTo(retainedScope.Session.SessionId);
        _ = await Assert.That(retainedScope.Session.IsActive()).IsFalse();
        _ = await Assert.That(provider.Requests).Count().IsEqualTo(2);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Sibling_scope_follows_effective_graphs_and_all_retry_roles(bool startsAsLeaf, CancellationToken cancellationToken)
    {
        const string replacementChildren = """
            [{"name":"new-first","description":"First replacement description","payload":"first secret payload","acceptance_criteria":"first secret criteria"},{"name":"new-second","dependencies":["new-first"],"description":"Second replacement description","payload":"second secret payload","acceptance_criteria":"second secret criteria"}]
            """;
        var answers = new List<string>
        {
            "{\"result\":\"outer dependency result\",\"verdict\":\"accept\",\"evidence\":\"done\"}",
        };
        if (startsAsLeaf)
        {
            answers.Add("{\"result\":\"first result\",\"verdict\":\"reject_and_retry\",\"feedback\":\"retry instruction\",\"payload\":\"retry work\"}");
            answers.Add($"{{\"result\":\"split result\",\"verdict\":\"reject_and_retry\",\"feedback\":\"split task\",\"payload\":{replacementChildren}}}");
            answers.Add("{\"context\":\"prepared replacement\"}");
        }
        else
        {
            answers.Add($"{{\"context\":\"prepared replacement\",\"task_patch\":{{\"payload\":{replacementChildren}}}}}");
        }

        answers.Add("{\"result\":\"first dependency result\",\"verdict\":\"accept\",\"evidence\":\"done\"}");
        answers.Add("{\"result\":\"second result\",\"verdict\":\"accept\",\"evidence\":\"done\"}");
        answers.Add("{\"verdict\":\"reject_and_retry\",\"feedback\":\"replace children\",\"payload\":[{\"name\":\"only-child\",\"description\":\"Singleton replacement\",\"payload\":\"only work\",\"acceptance_criteria\":\"only proof\"}]}");
        answers.Add("{\"result\":\"only result\",\"verdict\":\"accept\",\"evidence\":\"done\"}");
        answers.Add("{\"verdict\":\"reject_and_retry\",\"feedback\":\"finish directly\",\"payload\":\"direct finish\"}");
        answers.Add("direct execution result");
        answers.Add("{\"verdict\":\"accept\",\"evidence\":\"done\"}");
        answers.Add("{\"result\":\"last result\",\"verdict\":\"accept\",\"evidence\":\"done\"}");
        var provider = new AgentTaskQueueProvider(answers);
        var runtime = Runtime(provider, cancellationToken);
        await using var registry = runtime.Registry;
        var initialPayload = startsAsLeaf
            ? "\"initial work\""
            : "[{\"name\":\"old-child\",\"description\":\"Stale child description\",\"payload\":\"old work\",\"acceptance_criteria\":\"old proof\"}]";
        var artifact = AgentTaskParser.ParseArtifact($$"""
            {"schema_version":1,"tasks":[
              {"name":"outer","description":"Outer sibling description","payload":"outer secret payload","acceptance_criteria":"outer secret criteria"},
              {"name":"target","dependencies":["outer"],"description":"Target description","payload":{{initialPayload}},"acceptance_criteria":"target proof"},
              {"name":"last","dependencies":["target"],"description":"Last sibling description","payload":"last secret payload","acceptance_criteria":"last secret criteria"}
            ]}
            """);

        var result = await new RunnerFixture(runtime, "sibling-transitions", 5, _broker, _repository).Runner
            .Run(artifact, cancellationToken);

        _ = await Assert.That(result.Status).IsEqualTo(AgentTaskExecutionStatus.Succeeded);
        _ = await Assert.That(provider.Requests).Count().IsEqualTo(answers.Count);
        foreach (var request in provider.Requests)
        {
            var prompt = request.Messages.Last(message => message.Role == LLMRole.User).Content;
            var taskStart = prompt.IndexOf("Task: ", StringComparison.Ordinal);
            var scope = prompt[..taskStart];
            var taskName = prompt[(taskStart + "Task: ".Length)..].Split('\n')[0];
            if (taskName == "only-child")
            {
                _ = await Assert.That(scope).IsEmpty();
                continue;
            }

            _ = await Assert.That(scope).Contains("Sibling tasks (out of scope):")
                .And.Contains("Do not implement or duplicate their work.")
                .And.Contains("You may consume dependency results")
                .And.DoesNotContain($"[{taskName}]")
                .And.DoesNotContain("secret payload")
                .And.DoesNotContain("secret criteria")
                .And.DoesNotContain("Stale child description");
            switch (taskName)
            {
                case "target":
                    _ = await Assert.That(scope).Contains("[outer] Outer sibling description\n[last] Last sibling description")
                        .And.DoesNotContain("replacement description");
                    _ = await Assert.That(prompt).Contains("[outer] outer dependency result");
                    break;
                case "new-first":
                case "new-second":
                    var sibling = taskName == "new-first" ? "[new-second] Second replacement description" : "[new-first] First replacement description";
                    _ = await Assert.That(scope).Contains(sibling)
                        .And.DoesNotContain("Outer sibling description")
                        .And.DoesNotContain("Target description")
                        .And.DoesNotContain("Last sibling description");
                    if (taskName == "new-second")
                    {
                        _ = await Assert.That(prompt).Contains("[new-first] first dependency result");
                    }

                    break;
                case "outer":
                    _ = await Assert.That(scope).Contains("[target] Target description\n[last] Last sibling description");
                    break;
                case "last":
                    _ = await Assert.That(scope).Contains("[outer] Outer sibling description\n[target] Target description");
                    break;
                default:
                    throw new InvalidOperationException($"Unexpected task: {taskName}");
            }
        }
    }

    [Test]
    public async Task Sibling_scope_renders_custom_templates_and_bounds_descriptions(CancellationToken cancellationToken)
    {
        var directory = Path.Combine(Path.GetTempPath(), "parrot-sibling-templates", Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(directory);
        try
        {
            var configurationPath = Path.Combine(directory, "config.yaml");
            const string configurationYaml = """
                prompt_templates:
                  agent-task.sibling-scope:
                    template: 'CUSTOM OUT OF SCOPE{items}\n'
                  agent-task.sibling-item:
                    template: '\n{name}: {description}'
                """;
            await File.WriteAllTextAsync(configurationPath, configurationYaml, cancellationToken);
            var templates = Configuration.Load(configurationPath, Path.Combine(directory, "predefined_config.yaml")).PromptTemplates;
            var provider = new AgentTaskQueueProvider([
                "{\"result\":\"first result\",\"verdict\":\"accept\",\"evidence\":\"done\"}",
                "{\"result\":\"second result\",\"verdict\":\"accept\",\"evidence\":\"done\"}",
            ]);
            var runtime = Runtime(provider, cancellationToken);
            await using var registry = runtime.Registry;
            var artifact = AgentTaskParser.ParseArtifact($$"""
                {"schema_version":1,"tasks":[{"name":"first","description":"First description","payload":"work","acceptance_criteria":"proof"},{"name":"second","dependencies":["first"],"description":"{{new string('x', (16 * 1024) + 1)}}","payload":"work","acceptance_criteria":"proof"}]}
                """);
            var runner = new AgentTaskGraphRunner(
                runtime.Router,
                runtime.ParentScope,
                runtime.Selection,
                new AgentTaskProgress(_broker, _repository, runtime.Parent.SessionId, "custom-siblings", TestDiagnosticLog.Instance),
                new AgentTaskConfig(5, true, templates),
                new HistoryForkBoundary.AfterCompletedHistory());

            var result = await runner.Run(artifact, cancellationToken);

            _ = await Assert.That(result.Status).IsEqualTo(AgentTaskExecutionStatus.Succeeded);
            var prompt = provider.Requests[0].Messages.Last(message => message.Role == LLMRole.User).Content;
            var scope = prompt[..prompt.IndexOf("Task: ", StringComparison.Ordinal)];
            _ = await Assert.That(scope).StartsWith("CUSTOM OUT OF SCOPE")
                .And.Contains($"second: {new string('x', 16 * 1024)}")
                .And.Contains("[truncated]")
                .And.DoesNotContain(new string('x', (16 * 1024) + 1))
                .And.DoesNotContain("Sibling tasks (out of scope):");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private AgentTaskProgressSnapshot[] ProgressEvents(string originToolCallId) =>
        [.. _repository.Replay()
            .Where(published => published.PayloadCase == Event.PayloadOneofCase.AgentTaskProgressSnapshot)
            .Select(published => published.AgentTaskProgressSnapshot)
            .Where(snapshot => snapshot.OriginToolCallId == originToolCallId)];

    private RuntimeContext Runtime(ILLMProvider provider, CancellationToken cancellationToken)
    {
        var model = new LLMModel("model", provider.Id);
        var providers = new ProviderRegistry(
            [provider],
            new Dictionary<string, IReadOnlyList<LLMModel>>(StringComparer.Ordinal) { [provider.Id] = [model] });
        var router = new ModelRouter(providers, new ModelRouting(new ModelAliasCatalog(providers, []), $"{provider.Id}/model"));
        var sessions = new AgentTaskTestSessionFactory(router);
        var registry = TestModels.Registry(
            sessions,
            _broker,
            _repository,
            new TestProfileFixture().Registry,
            TestModels.PromptTemplates,
            cancellationToken);
        _registries.Add(registry);
        var identity = AgentIdentity.Main("agent-task-parent", "parent", TestModels.PromptTemplates);
        using var dependencies = TestModels.Dependencies(identity, _broker, _repository, cancellationToken);
        using var parentScope = TestAgentSessionScope.Build(identity, AgentSessionParentLink.Root(), registry, TestModels.PromptTemplates, (sessionParentScope, _, children, childQuestions) => new AgentSession(
            identity,
            sessionParentScope,
            new ModelSelector($"{provider.Id}/model"),
            router,
            _broker,
            _repository,
            [],
            TestModels.EmptyToolDefinitions,
            TestModels.MaterializePrompt(identity, ".", "."),
            new ToolOutputBlobStore(Path.GetTempPath()),
            TestModels.CompactionGroupBlobs(),
            new Parrot.Context.Compactor(90, 30, 60_000, 1024, TestModels.PromptTemplates),
            new ProviderSessions(TestDiagnosticLog.Instance, "agent-test"),
            new Parrot.Context.ContextCadence(),
            TestModels.PromptTemplates,
            childQuestions,
            dependencies.ExitReminder,
            dependencies.Profile,
            new TestCompletionCallbacksFixture(childQuestions, dependencies.ActiveWorkReminder, dependencies.ExitReminder, _repository, _broker).Callbacks,
            new SecurityProfileTestFixture(SecurityProfile.Compose(false, [], [], [])).Security,
            dependencies.Status,
            dependencies.Queues,
            new AgentSessionActivity(TimeProvider.System),
            TestDiagnosticLog.Instance,
            cancellationToken));
        registry.RegisterRootScope(parentScope);
        var parent = parentScope.Session;
        var selected = parent.CurrentSelection();
        var selection = new AgentTurnSelection(
            selected.RequestedModel,
            router.Resolve(selected.RequestedModel.Value),
            selected.Mode,
            selected.SecurityProfile);
        return new RuntimeContext(router, sessions, registry, parentScope, parent, selection);
    }

    private sealed class RunnerFixture(
        RuntimeContext runtime,
        string originToolCallId,
        int maximumAttempts,
        EventBroker broker,
        EventRepository repository)
    {
        internal AgentTaskGraphRunner Runner { get; } = new(
            runtime.Router,
            runtime.ParentScope,
            runtime.Selection,
            new AgentTaskProgress(broker, repository, runtime.Parent.SessionId, originToolCallId, TestDiagnosticLog.Instance),
            new AgentTaskConfig(maximumAttempts, true, TestModels.PromptTemplates),
            new HistoryForkBoundary.AfterCompletedHistory());
    }

    private sealed record RuntimeContext(
        ModelRouter Router,
        AgentTaskTestSessionFactory Sessions,
        IAgentRegistry Registry,
        IAgentSessionScope ParentScope,
        IAgentSession Parent,
        AgentTurnSelection Selection);

    private sealed class DiagnosticChildFactory(IAgentSessionScope scope, bool fail) : IAgentSessionFactory
    {
        public EventRepository PrepareHistory(string agentSessionId, EventRepository repository) =>
            repository;

        public IAgentSessionScope Create(
            AgentIdentity identity,
            AgentSessionParentLink parentLink,
            ModelSelector model,
            EventBroker eventBroker,
            EventRepository eventRepository,
            IMode mode,
            SecurityProfile securityProfile,
            RuntimeStatus status,
            IAgentRegistry registry,
            CancellationToken lifetime) =>
            fail ? throw new InvalidOperationException("secret-exception") : scope;
    }
}
