using Parrot.Agent;
using Parrot.AgentTasks;
using Parrot.Config;
using Parrot.Context;
using Parrot.Diagnostics;
using Parrot.Events;
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
                configuration.RequestLimits,
                configuration.ReadOnlyExecCommandPrefixes,
                router,
                new CompositeSystemPromptProvider("test:task-scopes", []),
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
            var rejectedIdentity = AgentIdentity.Child("rejected-owner", root.Session.SessionId, root.Session.Name, "rejected", 1, AgentScope.Empty(configuration.PromptTemplates), configuration.PromptTemplates);
            var rejectedHistory = session.Registry.InitializeChildHistory(root.Session.SessionId, rejectedIdentity.SessionId, new HistoryForkBoundary.AfterCompletedHistory(), HistoryForkSelection.Parse("empty"));
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
                var identity = AgentIdentity.Child(name, root.Session.SessionId, root.Session.Name, name, 1, AgentScope.Empty(configuration.PromptTemplates), configuration.PromptTemplates);
                var child = session.Registry.CreateChildScope(
                    identity,
                    AgentSessionParentLink.Child(root, AgentCompletionDeliveryPolicy.RetainedOnly, session.Registry.ReserveRetainedAgent()),
                    new ModelSelector(model.Selector),
                    session.Mode,
                    session.Mode.Profile.SecurityProfile,
                    session.Registry.InitializeChildHistory(root.Session.SessionId, identity.SessionId, new HistoryForkBoundary.AfterCompletedHistory(), HistoryForkSelection.Parse("empty")),
                    session.Lifetime);
                _ = await Assert.That(root.ChildRegistry.TryAdd(child)).IsTrue();
                scopes.Add(child);
            }

            _ = await Assert.That(ReferenceEquals(scopes[0].GetService<IAgentTaskRunCatalog>(), scopes[1].GetService<IAgentTaskRunCatalog>())).IsFalse();
            _ = await Assert.That(ReferenceEquals(root.GetService<IAgentTaskRunCatalog>(), scopes[0].GetService<IAgentTaskRunCatalog>())).IsFalse();
            using var database = SessionDatabase.Open(":memory:");
            using var broker = new EventBroker();
            var repository = new EventRepository(database);
            var completions = new List<Completion>();
            var requests = new List<AgentTaskRunRequest>();
            using var call = new CancellationTokenSource();
            foreach (var scope in scopes)
            {
                var selection = scope.Session.CurrentSelection();
                var completion = new Completion(new AgentTaskRunCompletion(
                    scope.Session, new ToolOutputBlobStore(directory), configuration.PromptTemplates));
                var request = new AgentTaskRunRequest(
                    "shared-run",
                    scope.Session.Name,
                    AgentTaskParser.ParseArtifact("""
                        {"schema_version":1,"tasks":[{"name":"worker","description":"Run work","payload":"work","acceptance_criteria":"Done"}]}
                        """),
                    router,
                    scope,
                    new AgentTurnSelection(selection.RequestedModel, router.Resolve(selection.RequestedModel.Value), selection.Mode, selection.SecurityProfile),
                    new AgentTaskProgress(broker, repository, scope.Session.SessionId, "shared-run", TestDiagnosticLog.Instance),
                    configuration.AgentTasks,
                    new HistoryForkBoundary.AfterCompletedHistory(),
                    completion);
                scope.GetService<IAgentTaskRunCatalog>().Start(request, call.Token);
                requests.Add(request);
                completions.Add(completion);
                await provider.Arrived(cancellationToken);
            }

            await call.CancelAsync();
            _ = await Assert.That(() => scopes[0].GetService<IAgentTaskRunCatalog>().Start(requests[0], cancellationToken)).Throws<InvalidOperationException>();
            _ = await Assert.That(() => scopes[0].GetService<IAgentTaskRunCatalog>().Start(requests[1], cancellationToken)).Throws<InvalidOperationException>();
            foreach (var scope in scopes)
            {
                _ = await Assert.That(scope.GetService<IAgentTaskRunCatalog>().Snapshot().Single().OwnerAgentSessionId).IsEqualTo(scope.Session.SessionId);
                var reminder = new ActiveWorkCompletionReminder([new ChildAgentActiveWorkBlocker(scope.ChildRegistry, scope.Session.Identity), new ProcessActiveWorkBlocker(scope.GetService<IProcessOwner>()), new AgentTaskActiveWorkBlocker(scope.GetService<IAgentTaskRunCatalog>(), configuration.PromptTemplates), new QueueActiveWorkBlocker(scope.GetService<IAgentQueues>(), configuration.PromptTemplates)], configuration.PromptTemplates).Build();
                _ = await Assert.That(reminder).Contains($"{scope.Session.SessionId}/shared-run");
                var status = await new AgentTaskStatusProvider(scope.GetService<IAgentTaskRunCatalog>(), configuration.PromptTemplates).Observe(
                    new StatusQuery(scope.Session.SessionId, root.Session.SessionId, root.Session.Name, "profile", "model"), cancellationToken);
                _ = await Assert.That(status.Available).IsTrue();
                _ = await Assert.That(status.Text).Contains(scope.Session.Name);
                _ = await Assert.That(status.Text).DoesNotContain(scopes.Single(other => !ReferenceEquals(other, scope)).Session.Name);
            }

            _ = await Assert.That(string.Join(',', session.Registry.SnapshotScopes().SelectMany(static scope => scope.GetService<IAgentTaskRunCatalog>().Active())
                .Select(work => work.Id).Order(StringComparer.Ordinal))).IsEqualTo("first-owner/shared-run,second-owner/shared-run");
            var detached = root.ChildRegistry.DetachDirectChildScope(scopes[0])
                ?? throw new InvalidOperationException("Scope shutdown already started.");
            await detached.DisposeAsync();
            await detached.DisposeAsync();
            _ = await Assert.That((await completions[0].Delivered.WaitAsync(cancellationToken)).Status).IsEqualTo(AgentTaskExecutionStatus.Canceled);
            _ = await Assert.That(scopes[0].GetService<IAgentTaskRunCatalog>().Active()).IsEmpty();
            _ = await Assert.That(scopes[1].GetService<IAgentTaskRunCatalog>().Active()).Count().IsEqualTo(1);
            _ = await Assert.That(completions[1].Delivered.IsCompleted).IsFalse();

            var nestedOwner = scopes[1].ChildRegistry.SnapshotChildScopes().Single();
            var nestedSelection = nestedOwner.Session.CurrentSelection();
            var nestedCompletion = new Completion(new AgentTaskRunCompletion(
                nestedOwner.Session, new ToolOutputBlobStore(directory), configuration.PromptTemplates));
            var nestedRequest = requests[1] with
            {
                OwnerScope = nestedOwner,
                Selection = new AgentTurnSelection(nestedSelection.RequestedModel, router.Resolve(nestedSelection.RequestedModel.Value), nestedSelection.Mode, nestedSelection.SecurityProfile),
                Progress = new AgentTaskProgress(broker, repository, nestedOwner.Session.SessionId, "shared-run", TestDiagnosticLog.Instance),
                Completion = nestedCompletion,
            };
            nestedOwner.GetService<IAgentTaskRunCatalog>().Start(nestedRequest, cancellationToken);
            await provider.Arrived(cancellationToken);
            _ = await Assert.That(session.Registry.SnapshotScopes().Sum(static scope => scope.GetService<IAgentTaskRunCatalog>().Active().Count)).IsEqualTo(2);
            await session.DisposeAsync();
            await session.DisposeAsync();
            _ = await Assert.That((await completions[1].Delivered.WaitAsync(cancellationToken)).Status).IsEqualTo(AgentTaskExecutionStatus.Canceled);
            _ = await Assert.That((await nestedCompletion.Delivered.WaitAsync(cancellationToken)).Status).IsEqualTo(AgentTaskExecutionStatus.Canceled);
            _ = await Assert.That(scopes[1].GetService<IAgentTaskRunCatalog>().Active()).IsEmpty();
            _ = await Assert.That(nestedOwner.GetService<IAgentTaskRunCatalog>().Active()).IsEmpty();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class Completion(IAgentTaskRunCompletion delivery) : IAgentTaskRunCompletion
    {
        private readonly TaskCompletionSource<AgentTaskRunTerminal> _delivered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task<AgentTaskRunTerminal> Delivered => _delivered.Task;

        public async Task Deliver(AgentTaskRunTerminal terminal, CancellationToken cancellationToken)
        {
            await delivery.Deliver(terminal, cancellationToken);
            _ = _delivered.TrySetResult(terminal);
        }

        public async Task DeliverDuringShutdown(AgentTaskRunTerminal terminal, CancellationToken cancellationToken)
        {
            await delivery.DeliverDuringShutdown(terminal, cancellationToken);
            _ = _delivered.TrySetResult(terminal);
        }
    }
}
