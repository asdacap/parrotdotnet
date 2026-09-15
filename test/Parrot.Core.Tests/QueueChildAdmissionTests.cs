using Parrot.Agent;
using Parrot.Queues;

namespace Parrot.Core.Tests;

internal sealed class QueueChildAdmissionTests
{
    internal enum InvalidAdmission
    {
        ParentMismatch,
        Shutdown,
    }

    [Test]
    public async Task Pre_existing_child_queue_overlap_is_rejected_before_admission()
    {
        var resources = TestModels.Resources();
        var source = new QueueTestScope(
            AgentIdentity.Main("parent", "parent", TestModels.PromptTemplates),
            null,
            resources);
        var child = new QueueTestScope(
            AgentIdentity.Child(
                "child",
                "parent",
                "parent",
                "worker",
                1,
                AgentScope.Empty(TestModels.PromptTemplates),
                TestModels.PromptTemplates),
            source,
            resources);
        _ = source.ChildRegistry.DetachDirectChildScope(child);

        try
        {
            var parentQueues = source.GetService<IAgentQueues>();
            var childQueues = child.GetService<IAgentQueues>();
            _ = childQueues.Local.Create("overlap", "child-owned");
            _ = parentQueues.Create("overlap", "parent-owned");

            _ = await Assert.That(() => source.ChildRegistry.TryAdd(child))
                .Throws<QueueAlreadyExistsException>();
            _ = await Assert.That(source.ChildRegistry.SnapshotChildScopes()).IsEmpty();
            _ = await Assert.That(parentQueues.Get("overlap").Description).IsEqualTo("parent-owned");
            _ = await Assert.That(childQueues.Get("overlap").Description).IsEqualTo("child-owned");
        }
        finally
        {
            await source.ChildRegistry.DisposeChildren().ConfigureAwait(false);
            await child.DisposeAsync().ConfigureAwait(false);
            await source.DisposeAsync().ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parent_queue_create_racing_child_admission_preserves_queue_exclusivity()
    {
        var resources = TestModels.Resources();
        var source = new QueueTestScope(
            AgentIdentity.Main("parent", "parent", TestModels.PromptTemplates),
            null,
            resources);
        var child = new QueueTestScope(
            AgentIdentity.Child(
                "child",
                "parent",
                "parent",
                "worker",
                1,
                AgentScope.Empty(TestModels.PromptTemplates),
                TestModels.PromptTemplates),
            source,
            resources);
        _ = source.ChildRegistry.DetachDirectChildScope(child);

        try
        {
            _ = child.GetService<IAgentQueues>().Create("racing", "child-owned");
            using var admissionReady = new ManualResetEventSlim();
            using var creationReady = new ManualResetEventSlim();
            using var start = new ManualResetEventSlim();
            var admission = Task.Run(() =>
            {
                admissionReady.Set();
                start.Wait();
                try
                {
                    return source.ChildRegistry.TryAdd(child);
                }
                catch (QueueAlreadyExistsException)
                {
                    return false;
                }
            });
            var creation = Task.Run(() =>
            {
                creationReady.Set();
                start.Wait();
                try
                {
                    _ = source.GetService<IAgentQueues>().Create("racing", "parent-owned");
                    return true;
                }
                catch (QueueAlreadyExistsException)
                {
                    return false;
                }
            });

            admissionReady.Wait();
            creationReady.Wait();
            start.Set();
            var outcomes = await Task.WhenAll(admission, creation).ConfigureAwait(false);

            _ = await Assert.That(outcomes.Count(static outcome => outcome)).IsEqualTo(1);
            var admissionSucceeded = outcomes[0];
            var parentOwnsQueue = source.GetService<IAgentQueues>().Local.List()
                .Any(queue => queue.Name == "racing");
            var childHasQueue = child.GetService<IAgentQueues>().Local.List()
                .Any(queue => queue.Name == "racing");
            var childIsAdmitted = source.ChildRegistry.SnapshotChildScopes().Contains(child);
            _ = await Assert.That(childHasQueue).IsTrue();
            _ = await Assert.That(childIsAdmitted).IsEqualTo(admissionSucceeded);
            _ = await Assert.That(parentOwnsQueue).IsEqualTo(!admissionSucceeded);
        }
        finally
        {
            if (source.ChildRegistry.SnapshotChildScopes().Contains(child))
            {
                _ = source.ChildRegistry.DetachDirectChildScope(child);
            }

            await source.ChildRegistry.DisposeChildren().ConfigureAwait(false);
            await child.DisposeAsync().ConfigureAwait(false);
            await source.DisposeAsync().ConfigureAwait(false);
        }
    }

    [Test]
    [Arguments(InvalidAdmission.ParentMismatch)]
    [Arguments(InvalidAdmission.Shutdown)]
    public async Task Invalid_parent_or_shutdown_check_precedes_queue_admission_validator(InvalidAdmission scenario)
    {
        var resources = TestModels.Resources();
        await using var source = new QueueTestScope(
            AgentIdentity.Main("parent", "parent", TestModels.PromptTemplates),
            null,
            resources);
        await using var other = scenario == InvalidAdmission.ParentMismatch
            ? new QueueTestScope(
                AgentIdentity.Main("other", "other", TestModels.PromptTemplates),
                null,
                resources)
            : null;
        var childParent = other ?? source;
        await using var child = new QueueTestScope(
            AgentIdentity.Child(
                "child",
                childParent.Session.Identity.SessionId,
                childParent.Session.Identity.Name,
                "worker",
                1,
                AgentScope.Empty(TestModels.PromptTemplates),
                TestModels.PromptTemplates),
            childParent,
            resources);
        var validatorInvoked = false;
        var target = new ChildRegistry(
            source.Session.Identity,
            candidate =>
            {
                validatorInvoked = true;
                QueueChildAdmissionValidator.Validate(candidate);
            });
        await using var targetLifetime = target;

        if (scenario == InvalidAdmission.ParentMismatch)
        {
            _ = await Assert.That(() => target.TryAdd(child))
                .Throws<AgentRegistryException>();
        }
        else
        {
            await target.DisposeChildren().ConfigureAwait(false);
            _ = await Assert.That(target.TryAdd(child)).IsFalse();
        }

        _ = await Assert.That(validatorInvoked).IsFalse();
        _ = await Assert.That(target.SnapshotChildScopes()).IsEmpty();
    }

    [Test]
    public async Task Admission_validator_runs_under_registry_gate_and_rejects_before_insertion()
    {
        var resources = TestModels.Resources();
        await using var source = new QueueTestScope(
            AgentIdentity.Main("parent", "parent", TestModels.PromptTemplates),
            null,
            resources);
        await using var child = new QueueTestScope(
            AgentIdentity.Child(
                "child",
                "parent",
                "parent",
                "worker",
                1,
                AgentScope.Empty(TestModels.PromptTemplates),
                TestModels.PromptTemplates),
            source,
            resources);
        var invoked = false;
        var gateHeld = false;
        ChildRegistry? targetChildren = null;
        var target = new ChildRegistry(
            source.Session.Identity,
            _ =>
            {
                invoked = true;
                gateHeld = targetChildren?.Gate.IsHeldByCurrentThread ?? false;
                throw new InvalidOperationException("rejected");
            });
        targetChildren = target;
        await using var targetLifetime = target;
        var failure = await Assert.That(() => target.TryAdd(child))
            .Throws<InvalidOperationException>();

        _ = await Assert.That(invoked).IsTrue();
        _ = await Assert.That(gateHeld).IsTrue();
        _ = await Assert.That(failure?.Message).IsEqualTo("rejected");
        _ = await Assert.That(target.SnapshotChildScopes()).IsEmpty();
        _ = await Assert.That(target.FindNamedChildScope("worker")).IsNull();
    }
}
