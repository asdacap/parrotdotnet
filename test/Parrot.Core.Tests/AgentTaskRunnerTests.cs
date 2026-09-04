using Parrot.Agent;
using Parrot.AgentTasks;
using Parrot.Config;
using Parrot.Events;
using Parrot.Llm;
using Parrot.Protocol;
using Parrot.Security;
using Parrot.Store;

namespace Parrot.Core.Tests;

internal sealed class AgentTaskRunnerTests : IDisposable
{
    private readonly SessionDatabase _database = SessionDatabase.Open(":memory:");
    private readonly EventBroker _broker = new();
    private readonly EventRepository _repository;

    public AgentTaskRunnerTests() => _repository = new EventRepository(_database);

    public void Dispose()
    {
        _broker.Dispose();
        _database.Dispose();
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

        var result = await Runner(runtime, "runner-call")
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
        _ = await Assert.That(provider.Requests).Count().IsEqualTo(1);
        _ = await Assert.That(provider.Requests[0].Messages.Count(message => message.Role != LLMRole.System)).IsEqualTo(1);
        var prompt = provider.Requests[0].Messages.Single(message => message.Role == LLMRole.User).Content;
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

        _ = await Runner(runtime, "acceptance-contract")
            .Run(artifact, cancellationToken);

        var prompt = provider.Requests[0].Messages.Single(message => message.Role == LLMRole.User).Content;
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

        var result = await Runner(runtime, "invalid-leaf-response")
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

        var result = await Runner(runtime, "bounded-leaf-response")
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

        var result = await RunnerWithAttempts(runtime, "halt-contract", 2)
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

        var result = await Runner(runtime, "runner-call")
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

        var result = await RunnerWithAttempts(runtime, "runner-call", 2)
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

        var result = await Runner(runtime, "runner-call")
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

        var result = await Runner(runtime, "replacement-context")
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

        var result = await Runner(runtime, "retained-context")
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

        var result = await RunnerWithAttempts(runtime, "final-context", 1)
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

        var result = await Runner(runtime, "runner-call")
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

        var result = await Runner(runtime, "preparation-replacement")
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

        _ = await Runner(runtime, "description-replacement")
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

        var result = await Runner(runtime, "retry-replacement")
            .Run(artifact, cancellationToken);

        _ = await Assert.That(result.Status).IsEqualTo(AgentTaskExecutionStatus.Succeeded);
        _ = await Assert.That(result.Tasks.Single().Context).IsEqualTo("composite preparation context");
        _ = await Assert.That(result.Tasks.Single().Tasks?.Single().Result).IsEqualTo("retry child result");
        _ = await Assert.That(result.Tasks.Single().RetryFeedback?.Single()).IsEqualTo("split it");
        var identities = runtime.Sessions.Identities;
        _ = await Assert.That(identities).Count().IsEqualTo(3);
        var composite = identities.Zip(runtime.Sessions.ProfileIds)
            .Single(agent => agent.Second == "agent-task-prepare").First;
        var replacementChild = identities.Single(identity => identity.Name == "retry-child");
        _ = await Assert.That(composite.Name).DoesNotContain("prepare");
        _ = await Assert.That(composite.ParentSessionId).IsEqualTo(runtime.Parent.SessionId);
        _ = await Assert.That(replacementChild.ParentSessionId).IsEqualTo(composite.SessionId);
        _ = await Assert.That(provider.Requests[^1].Messages.Count(message => message.Role != LLMRole.System)).IsEqualTo(3);
        _ = await Assert.That(identities.Select((identity, index) => runtime.Sessions.ProfileIds[index])
            .Count(profile => profile == "agent-task-prepare")).IsEqualTo(1);
        _ = await Assert.That(identities.Select((identity, index) => runtime.Sessions.ProfileIds[index])
            .Count(profile => profile == "agent-task-validation")).IsEqualTo(0);
        var preparationPrompt = provider.Requests[1].Messages.Single(message => message.Role == LLMRole.User).Content;
        _ = await Assert.That(preparationPrompt).Contains("replacement parent result");
        var childExecution = provider.Requests[2].Messages.Single(message => message.Role == LLMRole.User).Content;
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

        var result = await Runner(runtime, "recursive-composites")
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
        _ = await Assert.That(provider.Requests[3].Messages.Count(message => message.Role != LLMRole.System)).IsEqualTo(3);
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

        var result = await Runner(runtime, "dependency-evidence")
            .Run(artifact, cancellationToken);

        _ = await Assert.That(result.Status).IsEqualTo(AgentTaskExecutionStatus.Succeeded);
        var dependentPrompt = provider.Requests[2].Messages.Single(message => message.Role == LLMRole.User).Content;
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

        var graphResult = await Runner(runtime, "composite-dependency-result")
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

        var dependentPrompt = provider.Requests[8].Messages.Single(message => message.Role == LLMRole.User).Content;
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

        var result = await Runner(runtime, "blocked-subtree")
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
              {"name":"root","description":"Root","payload":[
                {"name":"fast","description":"Fast","payload":"fast work","acceptance_criteria":"Done"},
                {"name":"slow","description":"Slow","payload":"slow work","acceptance_criteria":"Done"},
                {"name":"dependent","dependencies":["fast"],"description":"Dependent","payload":"dependent work","acceptance_criteria":"Done"}
              ],"acceptance_criteria":"Root done"}
            ]}
            """);

        var result = await Runner(runtime, "runner-call")
            .Run(artifact, cancellationToken);

        _ = await Assert.That(result.Status).IsEqualTo(AgentTaskExecutionStatus.Succeeded);
        _ = await Assert.That(provider.MaximumActive >= 2).IsTrue();
        _ = await Assert.That(provider.DependentStartedBeforeSlowFinished).IsTrue();
        _ = await Assert.That(result.Tasks).HasSingleItem();
        var scheduledTasks = result.Tasks[0].Tasks
            ?? throw new InvalidOperationException("Root composite tasks were not produced.");
        _ = await Assert.That(string.Join(",", scheduledTasks.Select(task => task.Name))).IsEqualTo("fast,slow,dependent");
        var snapshots = ProgressEvents("runner-call");
        _ = await Assert.That(snapshots.All(snapshot =>
            snapshot.RootNodes.Count == 1 && string.Join(',', snapshot.RootNodes[0].Children.Select(node => node.Name)) == "fast,slow,dependent"))
            .IsTrue();
        var fastSucceeded = snapshots.ToList().FindIndex(snapshot =>
            snapshot.RootNodes[0].Children[0].Status == AgentTaskProgressStatus.Succeeded);
        var dependentRunning = snapshots.ToList().FindIndex(snapshot =>
            snapshot.RootNodes[0].Children[2].Status == AgentTaskProgressStatus.Running);
        _ = await Assert.That(dependentRunning > fastSucceeded).IsTrue();
    }

    [Test]
    public async Task Failed_role_reports_failure_and_releases_active_internal_child(CancellationToken cancellationToken)
    {
        var provider = new TerminalFailureProvider("role failed");
        var runtime = Runtime(provider, cancellationToken);
        await using var registry = runtime.Registry;
        var artifact = AgentTaskParser.ParseArtifact("""
            {"schema_version":1,"tasks":[{"name":"failed-role","description":"Fail task","payload":"work","acceptance_criteria":"Done"}]}
            """);

        var result = await Runner(runtime, "failed-role")
            .Run(artifact, cancellationToken);

        var task = result.Tasks.Single();
        _ = await Assert.That(result.Status).IsEqualTo(AgentTaskExecutionStatus.Failed);
        _ = await Assert.That(task.Status).IsEqualTo(AgentTaskExecutionStatus.Failed);
        _ = await Assert.That(task.Failure).IsEqualTo("execution agent failed: role failed");
        _ = await Assert.That(runtime.ParentScope.ChildRegistry.SnapshotDescendants().Where(session => session.IsActive())).IsEmpty();
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
        var running = Runner(runtime, "runner-call")
            .Run(artifact, canceled.Token);
        await provider.WaitUntilArrived(cancellationToken);

        await canceled.CancelAsync();
        _ = await Assert.That(running).Throws<OperationCanceledException>();

        _ = await Assert.That(runtime.ParentScope.ChildRegistry.SnapshotDescendants().Where(session => session.IsActive())).IsEmpty();
        var snapshots = ProgressEvents("runner-call");
        _ = await Assert.That(snapshots[^1].RootNodes.Single().Status)
            .IsEqualTo(AgentTaskProgressStatus.Canceled);
        _ = await Assert.That(snapshots[^1].Revision).IsEqualTo((ulong)snapshots.Length);
        _ = await Assert.That(_repository.Replay()[^1].AgentTaskProgressSnapshot.Revision)
            .IsEqualTo(snapshots[^1].Revision);
    }

    private AgentTaskProgressSnapshot[] ProgressEvents(string originToolCallId) =>
        [.. _repository.Replay()
            .Where(published => published.PayloadCase == Event.PayloadOneofCase.AgentTaskProgressSnapshot)
            .Select(published => published.AgentTaskProgressSnapshot)
            .Where(snapshot => snapshot.OriginToolCallId == originToolCallId)];

    private AgentTaskGraphRunner Runner(
        RuntimeContext runtime,
        string originToolCallId) => RunnerWithAttempts(runtime, originToolCallId, 5);

    private AgentTaskGraphRunner RunnerWithAttempts(
        RuntimeContext runtime,
        string originToolCallId,
        int maximumAttempts) => new(
            runtime.Router,
            runtime.ParentScope,
            runtime.Selection,
            new AgentTaskProgress(
                _broker,
                _repository,
                runtime.Parent.SessionId,
                originToolCallId),
            new AgentTaskConfig(maximumAttempts, TestModels.PromptTemplates));

    private RuntimeContext Runtime(ILLMProvider provider, CancellationToken cancellationToken)
    {
        var model = new LLMModel("model", provider.Id);
        var providers = new ProviderRegistry(
            [provider],
            new Dictionary<string, IReadOnlyList<LLMModel>>(StringComparer.Ordinal) { [provider.Id] = [model] });
        var router = new ModelRouter(providers, new ModelAliasCatalog(providers, []), $"{provider.Id}/model");
        var sessions = new AgentTaskTestSessionFactory(router);
        var registry = TestModels.Registry(
            sessions,
            _broker,
            _repository,
            TestModels.ProfileRegistry(),
            TestModels.PromptTemplates,
            cancellationToken);
        var identity = AgentIdentity.Main("agent-task-parent", "parent", TestModels.PromptTemplates);
        using var dependencies = TestModels.Dependencies(identity, _broker, _repository, cancellationToken);
        var parentScope = AgentSessionDirectScope.Build(identity, AgentSessionParentLink.Root(), registry, TestModels.PromptTemplates, (sessionParentScope, _, children, childQuestions) => new AgentSession(
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
            new Parrot.Context.ContextCadence(),
            TestModels.PromptTemplates,
            childQuestions,
            dependencies.ExitReminder,
            dependencies.Profile,
            TestModels.CompletionCallbacks(
                childQuestions,
                dependencies.ActiveWorkReminder,
                dependencies.ExitReminder,
                _repository,
                _broker),
            SecurityProfileTestFactory.Create(SecurityProfile.Compose(false, [], [], [])),
            dependencies.Status,
            dependencies.Queues,
            new AgentSessionActivity(TimeProvider.System),
            cancellationToken));
        registry.RegisterRootScope(parentScope);
        var parent = parentScope.Session;
        var selected = parent.Selection();
        var selection = new AgentTurnSelection(
            selected.RequestedModel,
            router.Resolve(selected.RequestedModel.Value),
            selected.Profile,
            selected.SecurityProfile);
        return new RuntimeContext(router, sessions, registry, parentScope, parent, selection);
    }

    private sealed record RuntimeContext(
        ModelRouter Router,
        AgentTaskTestSessionFactory Sessions,
        AgentRegistry Registry,
        IAgentSessionScope ParentScope,
        IAgentSession Parent,
        AgentTurnSelection Selection);
}
