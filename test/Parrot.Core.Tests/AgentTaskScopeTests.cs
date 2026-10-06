using Parrot.Agent;
using Parrot.AgentTasks;
using Parrot.Config;
using Parrot.Context;
using Parrot.Diagnostics;
using Parrot.Llm;
using Parrot.Process;
using Parrot.Queues;
using Parrot.Skills;
using Parrot.State;
using Parrot.Statuses;
using Parrot.Store;
using Parrot.Web;

namespace Parrot.Core.Tests;

internal sealed class AgentTaskScopeTests
{
    [Test]
    public async Task Real_scopes_isolate_runs_and_settle_nested_work_before_user_session_resources(
        CancellationToken cancellationToken)
    {
        var directory = Path.Combine(Path.GetTempPath(), "parrot-task-scopes", Guid.NewGuid().ToString("n"));
        _ = Directory.CreateDirectory(directory);
        try
        {
            var paths = new StatePaths(Path.Combine(directory, "state"), Path.Combine(directory, "config"), Path.Combine(directory, "data"));
            using var diagnostics = new DiagnosticLogs(paths, FileDiagnosticLog.CreateInstanceId(), TextWriter.Null, TimeProvider.System);
            var configuration = Configuration.Load(paths.ConfigFile, paths.PredefinedConfigFile);
            using var provider = new SteppedProvider();
            var model = new ProviderModel(provider, new LLMModel("model", provider.Id));
            var router = TestModels.Route(model);
            var profiles = new ProfileRegistry(configuration.Profiles, configuration.SandboxRules, [], configuration.DisabledTools);
            var modes = new ModeRegistry(profiles, configuration.DefaultProfile);
            var source = new AgentSessionFactorySource(
                ProcessRunner.Locate(ExecutableLocator.Capture()),
                new Compactor(90, 30, 60_000, 1024, configuration.PromptTemplates),
                WebFetcher.Create(new PublicWebAddressPolicy()),
                configuration.ToolDefinitions,
                configuration.AgentTasks,
                configuration.AgentSend,
                configuration.RequestLimits,
                configuration.ReadOnlyExecCommandPrefixes,
                router,
                [],
                configuration.PromptTemplates,
                static (arguments, scope) => new AgentSessionComposition(arguments, scope));
            var factory = new UserSessionFactory(
                source,
                modes,
                configuration.PromptTemplates,
                profiles,
                new SkillCatalogFactory(configuration, directory, Path.Combine(directory, "skills")),
                TimeSpan.FromSeconds(30),
                TimeProvider.System,
                AgentTaskParser.ParseArtifact);
            var store = new SessionStore(paths, directory, "host", factory, router, modes, diagnostics);
            await using var session = await store.Open(router.Resolve(model.Selector));
            var root = session.Registry.SnapshotScopes().Single();
            var rejectedIdentity = AgentIdentity.Child("rejected-owner", root.Session.Identity, "rejected", 1, AgentScope.Empty(configuration.PromptTemplates), configuration.PromptTemplates);
            var rejectedHistory = session.Registry.InitializeChildHistory(rejectedIdentity, new HistoryForkBoundary.AfterCompletedHistory(), HistoryForkSelection.Parse("empty"), new AgentHistorySource.Parent());
            _ = await Assert.That(() => session.Registry.CreateChildScope(
                rejectedIdentity,
                AgentSessionParentLink.Root(),
                new ModelSelector(model.Selector),
                session.Mode,
                session.Mode.Profile.SecurityProfile,
                rejectedHistory,
                session.Lifetime)).Throws<AgentRegistryException>();
            _ = await Assert.That(session.Registry.SnapshotScopes()).Count().IsEqualTo(1);
            var scopes = new List<IAgentSessionScope>();
            foreach (var name in new[] { "first-owner", "second-owner" })
            {
                var identity = AgentIdentity.Child(name, root.Session.Identity, name, 1, AgentScope.Empty(configuration.PromptTemplates), configuration.PromptTemplates);
                var child = session.Registry.CreateChildScope(
                    identity,
                    AgentSessionParentLink.Child(root, AgentCompletionDeliveryPolicy.RetainedOnly, session.Registry.ReserveRetainedAgent()),
                    new ModelSelector(model.Selector),
                    session.Mode,
                    session.Mode.Profile.SecurityProfile,
                    session.Registry.InitializeChildHistory(identity, new HistoryForkBoundary.AfterCompletedHistory(), HistoryForkSelection.Parse("empty"), new AgentHistorySource.Parent()),
                    session.Lifetime);
                _ = await Assert.That(root.ChildRegistry.TryAdd(child)).IsTrue();
                scopes.Add(child);
            }

            _ = await Assert.That(ReferenceEquals(scopes[0].GetService<IAgentTaskService>(), scopes[1].GetService<IAgentTaskService>())).IsFalse();
            _ = await Assert.That(ReferenceEquals(root.GetService<IAgentTaskService>(), scopes[0].GetService<IAgentTaskService>())).IsFalse();
            var tasks = AgentTaskParser.ParseTaskSet("""
                [{"name":"worker","description":"Run work","payload":"work","acceptance_criteria":"Done"}]
                """);
            foreach (var scope in scopes)
            {
                Set(scope, router, tasks);
                await provider.Arrived(cancellationToken);
            }

            foreach (var scope in scopes)
            {
                var agentTasks = scope.GetService<IAgentTaskService>();
                _ = await Assert.That(States(agentTasks)).IsEqualTo("worker:Running");
                var reminder = new ActiveWorkCompletionReminder([new ChildAgentActiveWorkBlocker(scope.ChildRegistry, scope.Session.Identity), new ProcessActiveWorkBlocker(scope.GetService<IProcessOwner>()), new AgentTaskActiveWorkBlocker(agentTasks, configuration.PromptTemplates), new QueueActiveWorkBlocker(scope.GetService<IAgentQueues>(), configuration.PromptTemplates)], configuration.PromptTemplates).Build();
                _ = await Assert.That(reminder).Contains("- worker [running] (name: Run work)");
                var status = await new AgentTaskStatusProvider(agentTasks, configuration.PromptTemplates).Observe(
                    new StatusQuery(scope.Session.SessionId, root.Session.SessionId, root.Session.Name, "profile", "model"), cancellationToken);
                _ = await Assert.That(status.Available).IsTrue();
                _ = await Assert.That(status.Text).Contains($"AgentTask graph: {scope.Session.SessionId}");
                _ = await Assert.That(status.Text).DoesNotContain(scopes.Single(other => !ReferenceEquals(other, scope)).Session.SessionId);
            }

            var detached = root.ChildRegistry.DetachDirectChildScope(scopes[0])
                ?? throw new InvalidOperationException("Scope shutdown already started.");
            await detached.DisposeAsync();
            await detached.DisposeAsync();
            _ = await Assert.That(States(scopes[0].GetService<IAgentTaskService>())).IsEqualTo("worker:Canceled");
            _ = await Assert.That(States(scopes[1].GetService<IAgentTaskService>())).IsEqualTo("worker:Running");
            _ = await Assert.That(() => Set(scopes[0], router, tasks)).Throws<InvalidOperationException>();

            var nestedOwner = scopes[1].ChildRegistry.SnapshotChildScopes().Single();
            Set(nestedOwner, router, tasks);
            await provider.Arrived(cancellationToken);
            _ = await Assert.That(States(nestedOwner.GetService<IAgentTaskService>())).IsEqualTo("worker:Running");
            await session.DisposeAsync();
            await session.DisposeAsync();
            _ = await Assert.That(States(scopes[1].GetService<IAgentTaskService>())).IsEqualTo("worker:Canceled");
            _ = await Assert.That(States(nestedOwner.GetService<IAgentTaskService>())).IsEqualTo("worker:Canceled");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task Real_nested_graph_inherits_dependency_history_without_changing_ownership(CancellationToken cancellationToken)
    {
        var directory = Path.Combine(Path.GetTempPath(), "parrot-nested-forks", Guid.NewGuid().ToString("n"));
        _ = Directory.CreateDirectory(directory);
        try
        {
            IAgentSessionScope? rootScope = null;
            IModelRouter? taskRouter = null;
            var dependentArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var sourceReply = """{"result":"nested-source-marker","verdict":"accept"}""";
            var provider = new AgentTaskReplyProvider(async (request, token) =>
            {
                if (!AgentTaskReplyProvider.IsTaskAgent(request))
                {
                    return "noted";
                }

                var prompt = AgentTaskReplyProvider.Prompt(request);
                if (prompt.Contains("Task: composite\n", StringComparison.Ordinal))
                {
                    var composite = (rootScope ?? throw new InvalidOperationException("The root is not ready.")).ChildRegistry.SnapshotChildScopes().Single();
                    _ = await Assert.That(composite.GetService<IAgentTaskService>().Snapshot()).IsEmpty();
                    _ = await Assert.That(composite.ChildRegistry.SnapshotChildScopes()).IsEmpty();
                    const string marker = "The following inner task list is supplied as prompt data, not registered in your AgentTask graph:\n";
                    var start = prompt.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
                    var end = prompt.IndexOf("\nUse set_agent_tasks", start, StringComparison.Ordinal);
                    Set(composite, taskRouter ?? throw new InvalidOperationException("The router is not ready."), AgentTaskParser.ParseTaskSet(prompt[start..end]));
                    await dependentArrived.Task.WaitAsync(token);
                }
                else if (prompt.Contains("Task: dependent\n", StringComparison.Ordinal))
                {
                    dependentArrived.SetResult();
                }
                else
                {
                    return sourceReply;
                }

                return """{"result":"nested done","verdict":"accept"}""";
            });
            var paths = new StatePaths(Path.Combine(directory, "state"), Path.Combine(directory, "config"), Path.Combine(directory, "data"));
            using var diagnostics = new DiagnosticLogs(paths, FileDiagnosticLog.CreateInstanceId(), TextWriter.Null, TimeProvider.System);
            var configuration = Configuration.Load(paths.ConfigFile, paths.PredefinedConfigFile);
            var model = new ProviderModel(provider, new LLMModel("model", provider.Id));
            var router = TestModels.Route(model);
            var profiles = new ProfileRegistry(configuration.Profiles, configuration.SandboxRules, [], configuration.DisabledTools);
            var modes = new ModeRegistry(profiles, configuration.DefaultProfile);
            var source = new AgentSessionFactorySource(
                ProcessRunner.Locate(ExecutableLocator.Capture()),
                new Compactor(90, 30, 60_000, 1024, configuration.PromptTemplates),
                WebFetcher.Create(new PublicWebAddressPolicy()),
                configuration.ToolDefinitions,
                configuration.AgentTasks,
                configuration.AgentSend,
                configuration.RequestLimits,
                configuration.ReadOnlyExecCommandPrefixes,
                router,
                [],
                configuration.PromptTemplates,
                static (arguments, scope) => new AgentSessionComposition(arguments, scope));
            var factory = new UserSessionFactory(
                source,
                modes,
                configuration.PromptTemplates,
                profiles,
                new SkillCatalogFactory(configuration, directory, Path.Combine(directory, "skills")),
                TimeSpan.FromSeconds(30),
                TimeProvider.System,
                AgentTaskParser.ParseArtifact);
            var store = new SessionStore(paths, directory, "host", factory, router, modes, diagnostics);
            await using var session = await store.Open(router.Resolve(model.Selector));
            var root = session.Registry.SnapshotScopes().Single();
            rootScope = root;
            taskRouter = router;
            Set(root, router, AgentTaskParser.ParseTaskSet("""
                [{"name":"composite","description":"Own nested work","payload":[
                  {"name":"dependent","description":"Continue work","payload":"dependent work","dependencies":["source"],"acceptance_criteria":"Done"},
                  {"name":"source","description":"Prepare work","payload":"source work","acceptance_criteria":"Done"}
                ],"acceptance_criteria":"Done"}]
                """));
            await dependentArrived.Task.WaitAsync(cancellationToken);
            var dependent = provider.Requests.Single(request => AgentTaskReplyProvider.IsTaskAgent(request)
                && AgentTaskReplyProvider.Prompt(request).Contains("Task: dependent\n", StringComparison.Ordinal));
            _ = await Assert.That(dependent.Messages.Where(message => message.Role == LLMRole.Assistant).Select(message => message.Content))
                .Contains(sourceReply);
            _ = await Assert.That(AgentTaskReplyProvider.Prompt(dependent)).Contains("Result: nested-source-marker");
            var compositeScope = root.ChildRegistry.SnapshotChildScopes().Single();
            var nestedScopes = compositeScope.ChildRegistry.SnapshotChildScopes();
            _ = await Assert.That(nestedScopes.Select(scope => scope.Session.Name)).IsEquivalentTo(["source", "dependent"]);
            _ = await Assert.That(nestedScopes.All(scope => scope.Session.ParentSessionId == compositeScope.Session.SessionId)).IsTrue();
            while (root.GetService<IAgentTaskService>().Snapshot().Single().State != AgentTaskExecutionStatus.Succeeded
                || compositeScope.GetService<IAgentTaskService>().Snapshot().Any(task => task.State != AgentTaskExecutionStatus.Succeeded))
            {
                await Task.Delay(10, cancellationToken);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void Set(IAgentSessionScope scope, IModelRouter router, IReadOnlyList<AgentTask> tasks)
    {
        var selection = scope.Session.CurrentSelection();
        scope.GetService<IAgentTaskService>().SetTasks(
            tasks,
            new AgentTurnSelection(selection.RequestedModel, router.Resolve(selection.RequestedModel.Value), selection.Mode, selection.SecurityProfile),
            new HistoryForkBoundary.AfterCompletedHistory());
    }

    private static string States(IAgentTaskService agentTasks) =>
        string.Join(",", agentTasks.Snapshot().Select(task => $"{task.Name}:{task.State}"));
}
