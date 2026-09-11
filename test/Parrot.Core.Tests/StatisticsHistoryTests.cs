using System.Text.Json;
using Parrot.Agent;
using Parrot.Llm;
using Parrot.Protocol;
using Parrot.Store;

namespace Parrot.Core.Tests;

internal sealed class StatisticsHistoryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "parrot-statistics-history", Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Test]
    public async Task Durable_facts_reopen_without_aggregate_projection_or_current_pricing(CancellationToken cancellationToken)
    {
        var path = Path.Combine(_directory, "session.db");
        long revision;
        using (var database = SessionDatabase.Open(path))
        {
            revision = new EventRepository(database).AppendUsageFact(new Event
            {
                Id = "request",
                AgentSessionId = "main",
                RequestUsageRecorded = new RequestUsageRecorded
                {
                    RequestId = "request",
                    Provider = "provider",
                    Model = "model",
                    InputTokens = 100,
                    InputCost = 7,
                    OutputCost = 3,
                    ToolCallIds = { "call" },
                },
            });
        }

        using var reopened = SessionDatabase.Open(path);
        var repository = new EventRepository(reopened);
        var replay = repository.ReplayStatistics();
        _ = await Assert.That(replay.Revision).IsEqualTo(revision);
        _ = await Assert.That(replay.Agents["main"].Self.Totals.TotalCost).IsEqualTo(10);
        _ = await Assert.That(repository.FindRequestUsage("main", "call")?.RequestId).IsEqualTo("request");
        _ = await Assert.That(repository.FindRequestUsage("main", "missing")).IsNull();
        _ = await Assert.That(repository.AgentHistorySessionIds().Single()).IsEqualTo("main");
        _ = await Assert.That(repository.AgentHistory("main").OfType<AgentHistoryRequestEntry>().Single().TotalCost).IsEqualTo(10);
        ILLMProvider provider = new TerminalFailureProvider("unused");
        var changedPricing = new ProviderModel(provider, new LLMModel("model", provider.Id)
        {
            InputPrice = 2,
            OutputPrice = 3,
        });
        var resumed = new AgentSessionStatistics(replay.Agents["main"]);
        resumed.AddOwn(AgentUsageIncrement.FromCompletion(
            changedPricing, null, LLMEvent.Completed("stop", 1, 0, 1, string.Empty, [])));
        _ = await Assert.That(resumed.Capture().Self.Totals.TotalCost).IsEqualTo(15);
        _ = await Assert.That(repository.ReplayStatistics().Agents["main"].Self.Totals.TotalCost).IsEqualTo(10);
        using var count = reopened.Connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM agent_usage;";
        _ = await Assert.That(Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture)).IsEqualTo(0);
    }

    [Test]
    [Arguments(null)]
    [Arguments("high")]
    public async Task Facts_replay_tree_costs_and_project_request_history_without_charging_forks(string? effort)
    {
        using var database = SessionDatabase.Open(":memory:");
        var repository = new EventRepository(database);
        var main = new Event
        {
            Id = "main-request",
            AgentSessionId = "main",
            RequestUsageRecorded = new RequestUsageRecorded
            {
                RequestId = "main-request",
                Provider = "provider",
                Model = "model",
                InputTokens = 100,
                CachedInputTokens = 20,
                OutputTokens = 10,
                InputCost = 0.5,
                OutputCost = 0.25,
                ContextSize = 100,
                ContextLimit = 1000,
            },
        };
        if (effort is not null)
        {
            main.RequestUsageRecorded.Effort = effort;
        }

        var firstRevision = repository.AppendUsageFact(main);
        _ = await Assert.That(repository.AppendUsageFact(main)).IsEqualTo(firstRevision);
        repository.AppendConversation(
            new Event { Id = "message", AgentSessionId = "main" },
            ConversationOrigin.Model,
            LLMRole.Assistant,
            [ConversationPart.TextPart("answer")],
            [],
            string.Empty);
        repository.InitializeForkedAgentHistory(
            "main", "fork", new HistoryForkBoundary.AfterCompletedHistory(), HistoryForkSelection.Parse("full"));
        foreach (var (agentId, parentId) in new[] { ("child", "main"), ("grandchild", "child"), ("sibling", "main") })
        {
            var child = main.Clone();
            child.Id = agentId + "-request";
            child.AgentSessionId = agentId;
            child.RequestUsageRecorded.RequestId = child.Id;
            child.RequestUsageRecorded.ParentAgentSessionId = parentId;
            _ = repository.AppendUsageFact(child);
        }

        var tool = new Event
        {
            Id = "tool-start",
            AgentSessionId = "grandchild",
            ToolExecutionStarted = new ToolExecutionStarted
            {
                RequestId = "grandchild-request",
                AssistantSequence = 1,
                ToolCallId = "call",
                ToolName = "status",
                ParentAgentSessionId = "child",
                Provider = "provider",
                Model = "model",
            },
        };
        if (effort is not null)
        {
            tool.ToolExecutionStarted.Effort = effort;
        }

        var revision = repository.AppendUsageFact(tool);
        _ = repository.AppendUsageFact(tool);
        var replay = repository.ReplayStatistics();
        _ = await Assert.That(replay.Revision).IsEqualTo(revision);
        _ = await Assert.That(replay.Agents["main"].Self.Totals.InputTokens).IsEqualTo(100);
        _ = await Assert.That(replay.Agents["main"].Cumulative.Totals.InputTokens).IsEqualTo(400);
        _ = await Assert.That(replay.Agents["child"].Cumulative.Totals.InputTokens).IsEqualTo(200);
        _ = await Assert.That(replay.Agents["main"].Cumulative.Totals.TotalCost).IsEqualTo(3);
        _ = await Assert.That(replay.Agents["main"].Cumulative.Totals.ToolCalls).IsEqualTo(1);
        _ = await Assert.That(replay.Agents["main"].Cumulative.Models[new("provider", "model", effort)].InputTokens).IsEqualTo(400);
        _ = await Assert.That(replay.Agents.GetValueOrDefault("fork", AgentSessionStatisticsSnapshot.Empty).Self.Totals.InputTokens).IsEqualTo(0);
        _ = await Assert.That(repository.ModelHistory("grandchild")).IsEmpty();
        _ = await Assert.That(repository.AgentHistory("fork").OfType<AgentHistoryRequestEntry>()).IsEmpty();
        var history = repository.AgentHistory("grandchild").OfType<AgentHistoryRequestEntry>().Single();
        var json = JsonSerializer.Serialize<AgentHistoryEntry>(history, AgentHistoryJsonContext.Default.AgentHistoryEntry);
        var restored = JsonSerializer.Deserialize(json, AgentHistoryJsonContext.Default.AgentHistoryEntry);
        _ = await Assert.That(restored).IsEqualTo(history);
        _ = await Assert.That(history.ToolCalls).IsEqualTo(1);
        _ = await Assert.That(history.TotalCost).IsEqualTo(0.75);
        _ = await Assert.That(history.Effort).IsEqualTo(effort);
        _ = await Assert.That(repository.AgentHistory("main").OfType<AgentHistoryRequestEntry>().Count()).IsEqualTo(1);
        _ = await Assert.That(repository.AgentHistory("main").OfType<AgentHistoryMessageEntry>().Count()).IsEqualTo(1);
        _ = repository.SaveCompaction("main", new CompactionSnapshot("summary", 1));
        _ = await Assert.That(repository.AgentHistory("main").OfType<AgentHistoryRequestEntry>().Single().InputTokens).IsEqualTo(100);
        _ = await Assert.That(new EventRepository(database).ReplayStatistics().Agents["main"].Self.Totals)
            .IsEqualTo(replay.Agents["main"].Self.Totals);
    }

    [Test]
    [Arguments("missing")]
    [Arguments("cycle")]
    [Arguments("conflict")]
    public async Task Replay_rejects_unrecoverable_lineage(string kind)
    {
        using var database = SessionDatabase.Open(":memory:");
        var repository = new EventRepository(database);
        _ = repository.AppendUsageFact(new Event
        {
            Id = "request",
            AgentSessionId = "child",
            RequestUsageRecorded = new RequestUsageRecorded
            {
                RequestId = "request",
                ParentAgentSessionId = kind == "cycle" ? "child" : "missing",
                InputTokens = 1,
            },
        });
        if (kind == "conflict")
        {
            _ = repository.AppendUsageFact(new Event
            {
                Id = "second",
                AgentSessionId = "child",
                RequestUsageRecorded = new RequestUsageRecorded { RequestId = "second", ParentAgentSessionId = "other" },
            });
        }

        _ = await Assert.That(() => repository.ReplayStatistics()).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task Legacy_uses_last_baseline_and_only_proven_execution_counts_before_new_facts()
    {
        using var database = SessionDatabase.Open(":memory:");
        var repository = new EventRepository(database);
        _ = repository.Append(
            new Event
            {
                Id = "started",
                AgentSessionId = "main",
                AgentStarted = new AgentStarted { Name = "main" },
            },
            null,
            null);
        foreach (var tokens in new[] { 10, 30 })
        {
            _ = repository.Append(
            new Event
            {
                Id = "legacy-" + tokens,
                AgentSessionId = "main",
                AgentStatisticsUpdated = new AgentStatisticsUpdatedEvent { InputTokens = tokens, InputCost = 2 },
            },
            null,
            null);
        }

        foreach (var call in new[] { "finished", "ambiguous" })
        {
            _ = repository.Append(
            new Event
            {
                Id = "start-" + call,
                AgentSessionId = "main",
                ToolStarted = new ToolStarted { ToolCallId = call },
            },
            null,
            null);
        }

        _ = repository.Append(
            new Event
            {
                Id = "finished",
                AgentSessionId = "main",
                ToolFinished = new ToolFinished { ToolCallId = "finished" },
            },
            null,
            null);
        _ = repository.AppendUsageFact(new Event
        {
            Id = "precise",
            AgentSessionId = "main",
            RequestUsageRecorded = new RequestUsageRecorded
            {
                RequestId = "precise",
                Provider = "provider",
                Model = "model",
                InputTokens = 5,
                InputCost = 0.125,
            },
        });
        var replay = repository.ReplayStatistics();
        _ = await Assert.That(replay.Agents["main"].Self.Totals.InputTokens).IsEqualTo(35);
        _ = await Assert.That(replay.Agents["main"].Self.Totals.TotalCost).IsEqualTo(2.125);
        _ = await Assert.That(replay.Agents["main"].Self.Models[AgentUsageKey.Legacy].ToolCalls).IsEqualTo(1);
        _ = await Assert.That(replay.IncompleteLegacyToolCounts.Contains("main")).IsTrue();
    }
}
