using System.Collections.Concurrent;
using System.Threading.Channels;
using Parrot.Cli.Enhanced;
using Parrot.Cli.Enhanced.Tools;
using Parrot.Llm;
using Parrot.Protocol;

namespace Parrot.Cli.Tests;

internal sealed class EnhancedHierarchyTests
{
    [Test]
    [Arguments("reset")]
    [Arguments("ended")]
    [Arguments("failed")]
    public async Task Child_request_phase_overrides_old_preview_without_scrollback_or_root_status(
        string ending, CancellationToken cancellationToken)
    {
        var drawn = string.Empty;
        var committed = new List<string>();
        var main = string.Empty;
        var context = new LiveBufferRenderContext(160, new TerminalPalette(false));
        await using var view = new RawActivityView(
            (items, _) =>
            {
                drawn = string.Join('|', items.SelectMany(item => item.Render(context).Lines).Select(line => line.Text));
                return Task.CompletedTask;
            },
            (item, _, _) =>
            {
                committed.Add(string.Join('|', item.Render(new ScrollbackRenderContext(160, context.Palette))));
                return Task.CompletedTask;
            },
            new ToolPresenterRegistry([], new GenericToolPresenter()),
            (label, _) =>
            {
                main = label;
                return Task.CompletedTask;
            });
        await view.Render(new Event { AgentSessionId = "root", TurnStarted = new TurnStarted() }, cancellationToken);
        await view.Render(
            new Event
            {
                AgentSessionId = "child",
                AgentStarted = new AgentStarted { ParentAgentSessionId = "root", Name = "worker" },
            },
            cancellationToken);
        await view.Render(new Event { AgentSessionId = "child", TurnStarted = new TurnStarted() }, cancellationToken);
        await view.Render(new Event { AgentSessionId = "child", TextChunk = new TextChunk { Fragment = "earlier preview" } }, cancellationToken);
        var rootLabel = main;
        foreach (var (phase, attempt) in new[]
        {
            (ProviderRequestPhase.Requesting, 1u), (ProviderRequestPhase.HeadersReceived, 1u),
            (ProviderRequestPhase.Requesting, 2u), (ProviderRequestPhase.HeadersReceived, 2u),
            (ProviderRequestPhase.Idle, 2u), (ProviderRequestPhase.HeadersReceived, 1u),
            (ProviderRequestPhase.Unspecified, 1u), (ProviderRequestPhase.HeadersReceived, 1u),
        })
        {
            await view.Render(
                new Event
                {
                    AgentSessionId = "child",
                    ProviderRequestPhaseChanged = new ProviderRequestPhaseChangedEvent { Phase = phase, Attempt = attempt },
                },
                cancellationToken);
            _ = await Assert.That(drawn.Contains("Requesting", StringComparison.Ordinal))
                .IsEqualTo(phase == ProviderRequestPhase.Requesting);
            if (phase == ProviderRequestPhase.Requesting)
            {
                _ = await Assert.That(drawn).Contains(attempt == 1 ? "Requesting…" : "Requesting (attempt 2)…");
            }

            _ = await Assert.That(drawn.Contains("Waiting for first token…", StringComparison.Ordinal))
                .IsEqualTo(phase == ProviderRequestPhase.HeadersReceived);
            if (phase == ProviderRequestPhase.HeadersReceived)
            {
                _ = await Assert.That(drawn).Contains("agent worker Waiting for first token…");
                _ = await Assert.That(drawn).DoesNotContain("earlier preview");
            }
            else if (phase != ProviderRequestPhase.Requesting)
            {
                _ = await Assert.That(drawn).Contains("earlier preview");
            }

            _ = await Assert.That(main).IsEqualTo(rootLabel);
        }

        if (ending == "reset")
        {
            await view.ResetRequests(cancellationToken);
            _ = await Assert.That(drawn).Contains("earlier preview");
        }
        else
        {
            var terminalEvent = new Event { AgentSessionId = "child" };
            if (ending == "ended")
            {
                terminalEvent.TurnEnded = new TurnEnded { FinishReason = "stop" };
            }
            else
            {
                terminalEvent.TurnFailed = new TurnFailed { Message = "failed" };
            }

            await view.Render(terminalEvent, cancellationToken);
        }

        _ = await Assert.That(drawn).DoesNotContain("Requesting…");
        _ = await Assert.That(drawn).DoesNotContain("Waiting for first token…");
        _ = await Assert.That(string.Join('|', committed)).DoesNotContain("Requesting…");
        _ = await Assert.That(string.Join('|', committed)).DoesNotContain("Waiting for first token…");
    }

    [Test]
    public async Task Agent_notices_are_committed_at_the_owning_agent_level(CancellationToken cancellationToken)
    {
        var committed = new List<string>();
        var liveContext = new LiveBufferRenderContext(120, new TerminalPalette(false));
        var scrollbackContext = new ScrollbackRenderContext(120, liveContext.Palette);

        Task Commit(
            IScrollbackItem item,
            IReadOnlyList<ILiveBufferItem> items,
            CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            committed.Add(string.Join('|', item.Render(scrollbackContext)));
            return Task.CompletedTask;
        }

        await using var view = new RawActivityView(
            static (_, _) => Task.CompletedTask,
            Commit,
            new ToolPresenterRegistry([], new GenericToolPresenter()),
            static (_, _) => Task.CompletedTask);
        await view.Render(
            new Event { AgentSessionId = "root", TurnStarted = new TurnStarted { Model = "model" } },
            cancellationToken);
        await view.Render(
            new Event
            {
                AgentSessionId = "child",
                AgentStarted = new AgentStarted { ParentAgentSessionId = "root", Name = "worker" },
            },
            cancellationToken);
        await view.Render(
            new Event { AgentSessionId = "child", ExitReminderInjected = new ExitReminderInjected() },
            cancellationToken);
        await view.Render(
            new Event
            {
                AgentSessionId = "child",
                SkillLoaded = new SkillLoadedEvent { Path = "/skills/example\u001b[2J/SKILL.md" },
            },
            cancellationToken);

        _ = await Assert.That(string.Join('|', committed)).IsEqualTo(
            "  ↻ [worker] Exit reminder injected|  ↻ [worker] Skill loaded: /skills/example[2J/SKILL.md");
    }

    [Test]
    public async Task Agent_completions_are_committed_immediately(
        CancellationToken cancellationToken)
    {
        var drawn = new List<string>();
        var committed = new List<string>();
        var liveContext = new LiveBufferRenderContext(120, new TerminalPalette(false));
        var scrollbackContext = new ScrollbackRenderContext(120, liveContext.Palette);

        Task Draw(IReadOnlyList<ILiveBufferItem> items, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            drawn.Add(string.Join('|', items.SelectMany(item => item.Render(liveContext).Lines).Select(line => line.Text)));
            return Task.CompletedTask;
        }

        Task Commit(
            IScrollbackItem item,
            IReadOnlyList<ILiveBufferItem> items,
            CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            committed.Add(string.Join('|', item.Render(scrollbackContext)));
            drawn.Add(string.Join('|', items.SelectMany(value => value.Render(liveContext).Lines).Select(line => line.Text)));
            return Task.CompletedTask;
        }

        await using var view = new RawActivityView(
            Draw,
            Commit,
            new ToolPresenterRegistry([], new GenericToolPresenter()),
            static (_, _) => Task.CompletedTask);
        await view.Render(
            new Event { AgentSessionId = "root", TurnStarted = new TurnStarted { Model = "model" } },
            cancellationToken);
        await view.Render(
            new Event
            {
                AgentSessionId = "parent",
                AgentStarted = new AgentStarted { ParentAgentSessionId = "root", Name = "parent" },
            },
            cancellationToken);
        await view.Render(
            new Event { AgentSessionId = "parent", TurnStarted = new TurnStarted { Model = "model" } },
            cancellationToken);
        await view.Render(
            new Event
            {
                AgentSessionId = "child",
                AgentStarted = new AgentStarted { ParentAgentSessionId = "parent", Name = "child" },
            },
            cancellationToken);
        await view.Render(
            new Event { AgentSessionId = "child", TurnStarted = new TurnStarted { Model = "model" } },
            cancellationToken);
        await view.Render(
            new Event { AgentSessionId = "parent", TextChunk = new TextChunk { Fragment = "parent response" } },
            cancellationToken);
        await view.Render(
            new Event { AgentSessionId = "child", TextChunk = new TextChunk { Fragment = "child\nresponse" } },
            cancellationToken);

        var live = drawn[^1];
        var liveRows = live.Split('|');
        var childRow = liveRows.Single(static row => row.Contains("[child]", StringComparison.Ordinal));
        var parentRow = liveRows.Single(static row => row.Contains("[parent]", StringComparison.Ordinal));
        _ = await Assert.That(childRow).IsEqualTo("    ● [child] child response");
        _ = await Assert.That(parentRow).IsEqualTo("  ● [parent] parent response");
        var childPosition = live.IndexOf(childRow, StringComparison.Ordinal);
        var parentPosition = live.IndexOf(parentRow, StringComparison.Ordinal);
        _ = await Assert.That(childPosition).IsGreaterThanOrEqualTo(0);
        _ = await Assert.That(parentPosition).IsGreaterThan(childPosition);
        _ = await Assert.That(live).DoesNotContain("    [child] response");

        await view.Render(
            new Event
            {
                AgentSessionId = "child",
                ToolStarted = new ToolStarted { ToolCallId = "tool", ToolName = "read" },
            },
            cancellationToken);
        await view.Render(
            new Event { AgentSessionId = "parent", TurnEnded = new TurnEnded { FinishReason = "stop" } },
            cancellationToken);
        _ = await Assert.That(string.Join('|', committed)).IsEqualTo("  ● [parent] parent response");
        await view.Render(
            new Event
            {
                AgentSessionId = "parent",
                AgentFinished = new AgentFinished
                {
                    ParentAgentSessionId = "root",
                    Name = "parent",
                    ElapsedMs = 65_000,
                },
            },
            cancellationToken);
        _ = await Assert.That(string.Join('|', committed))
            .IsEqualTo("  ● [parent] parent response|  ♟ [parent] agent finished (1m 05s)");
        _ = await Assert.That(drawn[^1]).DoesNotContain("[parent] agent finished");

        await view.Render(
            new Event { AgentSessionId = "child", TurnEnded = new TurnEnded { FinishReason = "stop" } },
            cancellationToken);
        _ = await Assert.That(string.Join('|', committed))
            .IsEqualTo("  ● [parent] parent response|  ♟ [parent] agent finished (1m 05s)|    ● [child] child|      [child] response");
        await view.Render(
            new Event
            {
                AgentSessionId = "child",
                ToolFinished = new ToolFinished { ToolCallId = "tool", ToolName = "read" },
            },
            cancellationToken);
        _ = await Assert.That(string.Join('|', committed))
            .IsEqualTo("  ● [parent] parent response|  ♟ [parent] agent finished (1m 05s)|    ● [child] child|      [child] response|    ✓ [child] tool call read");

        await view.Render(
            new Event
            {
                AgentSessionId = "child",
                AgentFinished = new AgentFinished
                {
                    ParentAgentSessionId = "parent",
                    Name = "child",
                    ElapsedMs = 7_000,
                },
            },
            cancellationToken);
        _ = await Assert.That(string.Join('|', committed))
            .IsEqualTo("  ● [parent] parent response|  ♟ [parent] agent finished (1m 05s)|    ● [child] child|      [child] response|    ✓ [child] tool call read|    ♟ [child] agent finished (7s)");
    }

    [Test]
    public async Task Child_agent_task_progress_without_an_active_tool_is_not_rendered(
        CancellationToken cancellationToken)
    {
        var committed = new List<string>();
        var drawn = new List<string>();
        var context = new ScrollbackRenderContext(80, new TerminalPalette(false));
        var liveContext = new LiveBufferRenderContext(80, context.Palette);

        Task Draw(IReadOnlyList<ILiveBufferItem> items, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            drawn.Add(string.Join('|', items.SelectMany(item => item.Render(liveContext).Lines).Select(line => line.Text)));
            return Task.CompletedTask;
        }

        Task Commit(IScrollbackItem item, IReadOnlyList<ILiveBufferItem> items, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            committed.Add(string.Join('|', item.Render(context)));
            drawn.Add(string.Join('|', items.SelectMany(value => value.Render(liveContext).Lines).Select(line => line.Text)));
            return Task.CompletedTask;
        }

        await using var view = new RawActivityView(
            Draw,
            Commit,
            new ToolPresenterRegistry([], new GenericToolPresenter()),
            static (_, _) => Task.CompletedTask);
        await view.Render(
            new Event { AgentSessionId = "main", TurnStarted = new TurnStarted { Model = "model" } },
            cancellationToken);
        await view.Render(
            new Event
            {
                AgentSessionId = "child",
                AgentStarted = new AgentStarted { ParentAgentSessionId = "main", Name = "worker" },
            },
            cancellationToken);
        await view.Render(
            new Event
            {
                AgentSessionId = "child",
                AgentTaskProgressSnapshot = new AgentTaskProgressSnapshot
                {
                    OriginToolCallId = "call",
                    Revision = 1,
                    RootNodes =
                    {
                        new AgentTaskProgressNode
                        {
                            Name = "root",
                            Status = AgentTaskProgressStatus.Running,
                            Children =
                            {
                                new AgentTaskProgressNode { Name = "child-a", Status = AgentTaskProgressStatus.Pending },
                                new AgentTaskProgressNode { Name = "child-b", Status = AgentTaskProgressStatus.Failed },
                            },
                        },
                    },
                },
            },
            cancellationToken);
        await view.Render(
            new Event
            {
                AgentSessionId = "child",
                AgentTaskProgressSnapshot = new AgentTaskProgressSnapshot
                {
                    OriginToolCallId = "call",
                    Revision = 1,
                    RootNodes =
                    {
                        new AgentTaskProgressNode
                        {
                            Name = "root",
                            Status = AgentTaskProgressStatus.Succeeded,
                            Children =
                            {
                                new AgentTaskProgressNode { Name = "child-a", Status = AgentTaskProgressStatus.Pending },
                                new AgentTaskProgressNode { Name = "child-b", Status = AgentTaskProgressStatus.Failed },
                            },
                        },
                    },
                },
            },
            cancellationToken);

        _ = await Assert.That(committed).IsEmpty();
        _ = await Assert.That(string.Join('|', drawn)).DoesNotContain("Agent tasks:");
    }

    [Test]
    public async Task Child_completion_renders_markdown_with_hierarchy_labels(
        CancellationToken cancellationToken)
    {
        var committed = new List<string>();
        var layouts = new List<ScrollbackLayout>();
        var scrollbackContext = new ScrollbackRenderContext(80, new TerminalPalette(false));

        Task Commit(
            IScrollbackItem item,
            IReadOnlyList<ILiveBufferItem> items,
            CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            _ = items;
            layouts.Add(item.Layout);
            committed.Add(string.Join('|', item.Render(scrollbackContext)));
            return Task.CompletedTask;
        }

        await using var view = new RawActivityView(
            static (_, token) =>
            {
                token.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            },
            Commit,
            new ToolPresenterRegistry([], new GenericToolPresenter()),
            static (_, _) => Task.CompletedTask);
        await view.Render(
            new Event { AgentSessionId = "root", TurnStarted = new TurnStarted { Model = "model" } },
            cancellationToken);
        await view.Render(
            new Event
            {
                AgentSessionId = "child",
                AgentStarted = new AgentStarted { ParentAgentSessionId = "root", Name = "child" },
            },
            cancellationToken);
        await view.Render(
            new Event { AgentSessionId = "child", TurnStarted = new TurnStarted { Model = "model" } },
            cancellationToken);
        await view.Render(
            new Event
            {
                AgentSessionId = "child",
                TextChunk = new TextChunk
                {
                    Fragment = "# Findings\n**bold** and `code`\n- first item\n```csharp\npublic var value = 42;\n```",
                },
            },
            cancellationToken);

        await view.Render(
            new Event { AgentSessionId = "child", TurnEnded = new TurnEnded { FinishReason = "stop" } },
            cancellationToken);
        await view.Render(
            new Event
            {
                AgentSessionId = "child",
                AgentFinished = new AgentFinished
                {
                    ParentAgentSessionId = "root",
                    Name = "child",
                    ElapsedMs = 7_000,
                },
            },
            cancellationToken);

        _ = await Assert.That(committed).Count().IsEqualTo(2);
        _ = await Assert.That(layouts[0]).IsEqualTo(ScrollbackLayout.Assistant);
        _ = await Assert.That(layouts[1]).IsEqualTo(ScrollbackLayout.Compact);
        _ = await Assert.That(committed[0]).IsEqualTo(
            "  ● [child] Findings|    [child] bold and code|    [child] • first item|" +
            "    [child] public var value = 42;");
        _ = await Assert.That(committed[0]).DoesNotContain("# Findings");
        _ = await Assert.That(committed[0]).DoesNotContain("**");
        _ = await Assert.That(committed[0]).DoesNotContain("```");
        _ = await Assert.That(committed[1]).IsEqualTo("  ♟ [child] agent finished (7s)");
    }

    [Test]
    public async Task Child_json_completion_renders_yaml_once_at_its_hierarchy_level(
        CancellationToken cancellationToken)
    {
        var committed = new List<string>();
        var scrollbackContext = new ScrollbackRenderContext(80, new TerminalPalette(false));

        Task Commit(
            IScrollbackItem item,
            IReadOnlyList<ILiveBufferItem> items,
            CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            _ = items;
            committed.Add(string.Join('|', item.Render(scrollbackContext)));
            return Task.CompletedTask;
        }

        await using var view = new RawActivityView(
            static (_, token) =>
            {
                token.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            },
            Commit,
            new ToolPresenterRegistry([], new GenericToolPresenter()),
            static (_, _) => Task.CompletedTask);
        await view.Render(
            new Event { AgentSessionId = "root", TurnStarted = new TurnStarted { Model = "model" } },
            cancellationToken);
        await view.Render(
            new Event
            {
                AgentSessionId = "child",
                AgentStarted = new AgentStarted { ParentAgentSessionId = "root", Name = "child" },
            },
            cancellationToken);
        await view.Render(
            new Event { AgentSessionId = "child", TurnStarted = new TurnStarted { Model = "model" } },
            cancellationToken);
        await view.Render(
            new Event
            {
                AgentSessionId = "child",
                TextChunk = new TextChunk { Fragment = "{\"answer\":{\"value\":1}}" },
            },
            cancellationToken);
        await view.Render(
            new Event { AgentSessionId = "child", TurnEnded = new TurnEnded { FinishReason = "stop" } },
            cancellationToken);
        await view.Render(
            new Event
            {
                AgentSessionId = "child",
                AgentFinished = new AgentFinished
                {
                    ParentAgentSessionId = "root",
                    Name = "child",
                    ElapsedMs = 7_000,
                },
            },
            cancellationToken);

        _ = await Assert.That(committed).Count().IsEqualTo(2);
        _ = await Assert.That(committed[0]).Contains("  ● [child] answer:");
        _ = await Assert.That(committed[0]).Contains("value: 1");
        _ = await Assert.That(committed[0]).DoesNotContain("{\\\"answer\\\"");
        _ = await Assert.That(committed[1]).IsEqualTo("  ♟ [child] agent finished (7s)");
    }

    [Test]
    public async Task Child_response_keeps_ten_lines_and_aligns_continuations_with_its_label(
        CancellationToken cancellationToken)
    {
        var drawn = new List<string>();
        var committed = new List<string>();
        var liveContext = new LiveBufferRenderContext(120, new TerminalPalette(false));
        var scrollbackContext = new ScrollbackRenderContext(120, liveContext.Palette);

        Task Draw(IReadOnlyList<ILiveBufferItem> items, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            drawn.Add(string.Join('|', items.SelectMany(item => item.Render(liveContext).Lines).Select(line => line.Text)));
            return Task.CompletedTask;
        }

        Task Commit(
            IScrollbackItem item,
            IReadOnlyList<ILiveBufferItem> items,
            CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            committed.Add(string.Join('|', item.Render(scrollbackContext)));
            return Task.CompletedTask;
        }

        await using var view = new RawActivityView(
            Draw,
            Commit,
            new ToolPresenterRegistry([], new GenericToolPresenter()),
            static (_, _) => Task.CompletedTask);
        await view.Render(
            new Event { AgentSessionId = "root", TurnStarted = new TurnStarted { Model = "model" } },
            cancellationToken);
        await view.Render(
            new Event
            {
                AgentSessionId = "child",
                AgentStarted = new AgentStarted { ParentAgentSessionId = "root", Name = "child" },
            },
            cancellationToken);
        await view.Render(
            new Event { AgentSessionId = "child", TurnStarted = new TurnStarted { Model = "model" } },
            cancellationToken);

        var response = string.Join('\n', Enumerable.Range(1, 12).Select(static line => $"line {line}"));
        var split = response.IndexOf("line 6", StringComparison.Ordinal) + "line ".Length;
        await view.Render(
            new Event { AgentSessionId = "child", TextChunk = new TextChunk { Fragment = response[..split] } },
            cancellationToken);
        await view.Render(
            new Event { AgentSessionId = "child", TextChunk = new TextChunk { Fragment = response[split..] } },
            cancellationToken);

        var expectedResponse = string.Join(
            '|',
            Enumerable.Range(1, 10).Select(static line => line == 1
                ? "  ● [child] line 1"
                : $"    [child] line {line}"));
        var liveResponse = drawn[^1].Split('|').Single(static row => row.Contains("[child]", StringComparison.Ordinal));
        _ = await Assert.That(liveResponse).IsEqualTo(
            "  ● [child] " + string.Join(' ', Enumerable.Range(1, 10).Select(static line => $"line {line}")));
        _ = await Assert.That(drawn[^1]).DoesNotContain("|  [child] line 2");
        _ = await Assert.That(drawn[^1]).DoesNotContain("line 11");

        await view.Render(
            new Event { AgentSessionId = "child", TurnEnded = new TurnEnded { FinishReason = "stop" } },
            cancellationToken);
        await view.Render(
            new Event
            {
                AgentSessionId = "child",
                AgentFinished = new AgentFinished
                {
                    ParentAgentSessionId = "root",
                    Name = "child",
                    ElapsedMs = 7_000,
                },
            },
            cancellationToken);

        _ = await Assert.That(committed[0]).IsEqualTo(expectedResponse);
        _ = await Assert.That(string.Join('|', committed)).DoesNotContain("line 11");
        _ = await Assert.That(committed[1]).IsEqualTo("  ♟ [child] agent finished (7s)");
    }

    [Test]
    public async Task Child_response_moves_on_text_events_but_not_animation_ticks(
        CancellationToken cancellationToken)
    {
        var drawn = new ConcurrentQueue<string>();
        var ticks = Channel.CreateUnbounded<bool>();
        var context = new LiveBufferRenderContext(18, new TerminalPalette(false));

        Task Draw(IReadOnlyList<ILiveBufferItem> items, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            drawn.Enqueue(Render(items, context));
            return Task.CompletedTask;
        }

        async Task Delay(CancellationToken token) =>
            _ = await ticks.Reader.ReadAsync(token);

        await using var view = new RawActivityView(
            Draw,
            static (_, _, token) =>
            {
                token.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            },
            Delay,
            new ToolPresenterRegistry([], new GenericToolPresenter()),
            static (_, _) => Task.CompletedTask);
        using var animating = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var animation = view.Run(animating.Token);

        await view.Render(
            new Event { AgentSessionId = "root", TurnStarted = new TurnStarted { Model = "model" } },
            cancellationToken);
        await view.Render(
            new Event
            {
                AgentSessionId = "writer",
                AgentStarted = new AgentStarted { ParentAgentSessionId = "root", Name = "writer" },
            },
            cancellationToken);
        await view.Render(
            new Event { AgentSessionId = "writer", TurnStarted = new TurnStarted { Model = "model" } },
            cancellationToken);
        await view.Render(
            new Event
            {
                AgentSessionId = "idle",
                AgentStarted = new AgentStarted { ParentAgentSessionId = "root", Name = "idle" },
            },
            cancellationToken);
        await view.Render(
            new Event { AgentSessionId = "idle", TurnStarted = new TurnStarted { Model = "model" } },
            cancellationToken);
        await view.Render(
            new Event { AgentSessionId = "writer", TextChunk = new TextChunk { Fragment = "old newest" } },
            cancellationToken);

        var beforeUpdate = drawn.Last().Split('|');
        var firstResponse = beforeUpdate.Single(static line => line.Contains("[writer]", StringComparison.Ordinal));
        _ = await Assert.That(firstResponse).IsEqualTo("  ● [writer] ewest");

        await view.Render(
            new Event { AgentSessionId = "writer", TextChunk = new TextChunk { Fragment = " word" } },
            cancellationToken);
        var beforeTick = drawn.Last().Split('|');
        var updatedResponse = beforeTick.Single(static line => line.Contains("[writer]", StringComparison.Ordinal));
        var spinnerBeforeTick = beforeTick.Single(static line => line.Contains("[idle]", StringComparison.Ordinal));
        _ = await Assert.That(updatedResponse).IsEqualTo("  ● [writer]  word");

        var drawCount = drawn.Count;
        await ticks.Writer.WriteAsync(true, cancellationToken);
        while (drawn.Count == drawCount)
        {
            await Task.Delay(1, cancellationToken);
        }

        var afterTick = drawn.Last().Split('|');
        var responseAfterTick = afterTick.Single(static line => line.Contains("[writer]", StringComparison.Ordinal));
        var spinnerAfterTick = afterTick.Single(static line => line.Contains("[idle]", StringComparison.Ordinal));
        _ = await Assert.That(responseAfterTick).IsEqualTo(updatedResponse);
        _ = await Assert.That(spinnerAfterTick).IsNotEqualTo(spinnerBeforeTick);

        await animating.CancelAsync();
        await animation;
    }

    [Test]
    public async Task Child_modeline_tools_fold_into_agent_status_and_defer_completion(
        CancellationToken cancellationToken)
    {
        var drawn = new List<string>();
        var committed = new List<string>();
        var liveContext = new LiveBufferRenderContext(120, new TerminalPalette(false));
        var scrollbackContext = new ScrollbackRenderContext(120, liveContext.Palette);

        Task Draw(IReadOnlyList<ILiveBufferItem> items, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            drawn.Add(string.Join('|', items.SelectMany(item => item.Render(liveContext).Lines).Select(line => line.Text)));
            return Task.CompletedTask;
        }

        Task Commit(IScrollbackItem item, IReadOnlyList<ILiveBufferItem> items, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            committed.Add(string.Join('|', item.Render(scrollbackContext)));
            return Task.CompletedTask;
        }

        await using var view = new RawActivityView(
            Draw,
            Commit,
            new ToolPresenterRegistry([], new GenericToolPresenter()),
            static (_, _) => Task.CompletedTask);
        await view.Render(
            new Event { AgentSessionId = "root", TurnStarted = new TurnStarted { Model = "model" } },
            cancellationToken);
        await view.Render(
            new Event
            {
                AgentSessionId = "child",
                AgentStarted = new AgentStarted { ParentAgentSessionId = "root", Name = "worker" },
            },
            cancellationToken);
        await view.Render(
            new Event { AgentSessionId = "child", TurnStarted = new TurnStarted { Model = "model" } },
            cancellationToken);
        await view.Render(
            new Event { AgentSessionId = "child", TextChunk = new TextChunk { Fragment = "completed work" } },
            cancellationToken);
        await view.Render(
            new Event { AgentSessionId = "child", TurnEnded = new TurnEnded { FinishReason = "stop" } },
            cancellationToken);
        await view.Render(
            new Event
            {
                AgentSessionId = "child",
                AgentFinished = new AgentFinished
                {
                    ParentAgentSessionId = "root",
                    Name = "worker",
                    ElapsedMs = 7_000,
                },
            },
            cancellationToken);

        _ = await Assert.That(string.Join('|', committed))
            .IsEqualTo("  ● [worker] completed work|  ♟ [worker] agent finished (7s)");
        _ = await Assert.That(drawn[^1]).DoesNotContain("agent main");
        _ = await Assert.That(drawn[^1]).DoesNotContain("Working:");
    }

    [Test]
    public async Task Failed_parent_turn_flushes_while_a_child_is_running(CancellationToken cancellationToken)
    {
        var committed = new List<string>();
        var context = new ScrollbackRenderContext(120, new TerminalPalette(false));

        Task Draw(IReadOnlyList<ILiveBufferItem> items, CancellationToken token) => Task.CompletedTask;

        Task Commit(
            IScrollbackItem item,
            IReadOnlyList<ILiveBufferItem> items,
            CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            committed.Add(string.Join('|', item.Render(context)));
            return Task.CompletedTask;
        }

        await using var view = new RawActivityView(
            Draw,
            Commit,
            new ToolPresenterRegistry([], new GenericToolPresenter()),
            static (_, _) => Task.CompletedTask);
        await view.Render(
            new Event { AgentSessionId = "root", TurnStarted = new TurnStarted { Model = "model" } },
            cancellationToken);
        await view.Render(
            new Event
            {
                AgentSessionId = "child",
                AgentStarted = new AgentStarted { ParentAgentSessionId = "root", Name = "worker" },
            },
            cancellationToken);
        await view.Render(
            new Event { AgentSessionId = "child", TurnStarted = new TurnStarted { Model = "model" } },
            cancellationToken);
        await view.Render(
            new Event
            {
                AgentSessionId = "root",
                TurnFailed = new TurnFailed { Message = "the turn exceeded its tool-call limit" },
            },
            cancellationToken);

        _ = await Assert.That(committed.Count).IsEqualTo(1);
        _ = await Assert.That(committed[0]).IsEqualTo("✗ agent: the turn exceeded its tool-call limit");
    }

    [Test]
    public async Task Child_terminal_events_retry_failed_commits(CancellationToken cancellationToken)
    {
        var committed = new List<string>();
        var failCommit = true;
        var context = new ScrollbackRenderContext(120, new TerminalPalette(false));

        Task Commit(IScrollbackItem item, IReadOnlyList<ILiveBufferItem> items, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            _ = items;
            if (failCommit)
            {
                failCommit = false;
                throw new InvalidOperationException("commit failed");
            }

            committed.Add(string.Join('|', item.Render(context)));
            return Task.CompletedTask;
        }

        await using var view = new RawActivityView(
            static (_, token) =>
            {
                token.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            },
            Commit,
            new ToolPresenterRegistry([], new GenericToolPresenter()),
            static (_, _) => Task.CompletedTask);
        await view.Render(
            new Event { AgentSessionId = "root", TurnStarted = new TurnStarted { Model = "model" } },
            cancellationToken);
        await view.Render(
            new Event
            {
                AgentSessionId = "child",
                AgentStarted = new AgentStarted { ParentAgentSessionId = "root", Name = "worker" },
            },
            cancellationToken);
        await view.Render(
            new Event { AgentSessionId = "child", TurnStarted = new TurnStarted { Model = "model" } },
            cancellationToken);
        await view.Render(
            new Event { AgentSessionId = "child", TextChunk = new TextChunk { Fragment = "completed work" } },
            cancellationToken);
        var turnEnded = new Event
        {
            AgentSessionId = "child",
            TurnEnded = new TurnEnded { FinishReason = "stop" },
        };

        _ = await Assert.That(async () => await view.Render(turnEnded, cancellationToken))
            .Throws<InvalidOperationException>();
        await view.Render(turnEnded, cancellationToken);

        var agentFinished = new Event
        {
            AgentSessionId = "child",
            AgentFinished = new AgentFinished
            {
                ParentAgentSessionId = "root",
                Name = "worker",
                ElapsedMs = 7_000,
            },
        };
        failCommit = true;
        _ = await Assert.That(async () => await view.Render(agentFinished, cancellationToken))
            .Throws<InvalidOperationException>();
        await view.Render(agentFinished, cancellationToken);
        await view.Render(agentFinished, cancellationToken);

        _ = await Assert.That(string.Join('|', committed))
            .IsEqualTo("  ● [worker] completed work|  ♟ [worker] agent finished (7s)");
    }

    [Test]
    public async Task Summary_reasoning_chunks_are_committed_individually(CancellationToken cancellationToken)
    {
        var committed = new List<string>();
        var context = new ScrollbackRenderContext(120, new TerminalPalette(false));

        Task Draw(IReadOnlyList<ILiveBufferItem> items, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        Task Commit(IScrollbackItem item, IReadOnlyList<ILiveBufferItem> items, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            committed.Add(string.Join('|', item.Render(context)));
            return Task.CompletedTask;
        }

        await using var view = new RawActivityView(
            Draw,
            Commit,
            new ToolPresenterRegistry([], new GenericToolPresenter()),
            static (_, _) => Task.CompletedTask);
        await view.Render(
            new Event { AgentSessionId = "root", TurnStarted = new TurnStarted { Model = "model" } },
            cancellationToken);
        await view.Render(
            new Event
            {
                AgentSessionId = "root",
                ReasoningChunk = new ReasoningChunk
                {
                    Fragment = "# first\n- **bold**",
                    Kind = ReasoningKind.Summary,
                },
            },
            cancellationToken);
        await view.Render(
            new Event
            {
                AgentSessionId = "root",
                ReasoningChunk = new ReasoningChunk { Fragment = "**second**", Kind = ReasoningKind.Summary },
            },
            cancellationToken);

        _ = await Assert.That(committed.Count).IsEqualTo(2);
        _ = await Assert.That(committed[0]).IsEqualTo("✦ first|  • bold");
        _ = await Assert.That(committed[1]).IsEqualTo("✦ second");
    }

    [Test]
    public async Task Child_reasoning_summaries_flush_the_child_response_and_keep_root_reasoning_state(
        CancellationToken cancellationToken)
    {
        var drawn = new List<string>();
        var committed = new List<string>();
        var failResponseCommit = false;
        var liveContext = new LiveBufferRenderContext(120, new TerminalPalette(false));
        var scrollbackContext = new ScrollbackRenderContext(120, liveContext.Palette);

        Task Draw(IReadOnlyList<ILiveBufferItem> items, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            drawn.Add(Render(items, liveContext));
            return Task.CompletedTask;
        }

        Task Commit(IScrollbackItem item, IReadOnlyList<ILiveBufferItem> items, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var rendered = string.Join('|', item.Render(scrollbackContext));
            if (failResponseCommit && rendered.Contains("Buffered response", StringComparison.Ordinal))
            {
                failResponseCommit = false;
                throw new InvalidOperationException("commit failed");
            }

            committed.Add(rendered);
            drawn.Add(Render(items, liveContext));
            return Task.CompletedTask;
        }

        await using var view = new RawActivityView(
            Draw,
            Commit,
            new ToolPresenterRegistry([], new GenericToolPresenter()),
            static (_, _) => Task.CompletedTask);
        await view.Render(
            new Event { AgentSessionId = "root", TurnStarted = new TurnStarted { Model = "model" } },
            cancellationToken);
        await view.Render(
            new Event
            {
                AgentSessionId = "child",
                AgentStarted = new AgentStarted { ParentAgentSessionId = "root", Name = "child" },
            },
            cancellationToken);
        await view.Render(
            new Event { AgentSessionId = "child", TurnStarted = new TurnStarted { Model = "model" } },
            cancellationToken);
        await view.Render(
            new Event
            {
                AgentSessionId = "grandchild",
                AgentStarted = new AgentStarted { ParentAgentSessionId = "child", Name = "grandchild" },
            },
            cancellationToken);
        await view.Render(
            new Event { AgentSessionId = "grandchild", TurnStarted = new TurnStarted { Model = "model" } },
            cancellationToken);
        await view.Render(
            new Event
            {
                AgentSessionId = "root",
                ReasoningChunk = new ReasoningChunk { Fragment = "private reasoning", Kind = ReasoningKind.Raw },
            },
            cancellationToken);
        await view.Render(
            new Event
            {
                AgentSessionId = "child",
                ReasoningChunk = new ReasoningChunk { Fragment = "Initial", Kind = ReasoningKind.Summary },
            },
            cancellationToken);
        await view.Render(
            new Event { AgentSessionId = "child", TextChunk = new TextChunk { Fragment = "Buffered response" } },
            cancellationToken);
        var findings = new Event
        {
            AgentSessionId = "child",
            ReasoningChunk = new ReasoningChunk
            {
                Fragment = "# Findings\n- **bold**",
                Kind = ReasoningKind.Summary,
            },
        };
        failResponseCommit = true;
        _ = await Assert.That(async () => await view.Render(findings, cancellationToken))
            .Throws<InvalidOperationException>();
        await view.Render(findings, cancellationToken);
        await view.Render(
            new Event
            {
                AgentSessionId = "grandchild",
                ReasoningChunk = new ReasoningChunk { Fragment = "Deep result", Kind = ReasoningKind.Summary },
            },
            cancellationToken);
        await view.Render(
            new Event
            {
                AgentSessionId = "child",
                ReasoningChunk = new ReasoningChunk { Fragment = string.Empty, Kind = ReasoningKind.Summary },
            },
            cancellationToken);

        _ = await Assert.That(committed).Count().IsEqualTo(4);
        _ = await Assert.That(committed[0]).IsEqualTo("  ✦ [child] Initial");
        _ = await Assert.That(committed[1]).IsEqualTo("  ● [child] Buffered response");
        _ = await Assert.That(committed[2]).IsEqualTo("  ✦ [child] Findings|    [child] • bold");
        _ = await Assert.That(committed[3]).IsEqualTo("    ✦ [grandchild] Deep result");
        _ = await Assert.That(drawn[^1]).Contains("Thinking (");
        _ = await Assert.That(string.Join('|', committed)).DoesNotContain("• [child] ✦");
    }

    [Test]
    public async Task Child_model_alias_icon_follows_only_its_current_agent_activity(
        CancellationToken cancellationToken)
    {
        var drawn = new List<IReadOnlyList<ILiveBufferItem>>();
        var committed = new List<string>();
        var context = new LiveBufferRenderContext(80, new TerminalPalette(false));
        var scrollbackContext = new ScrollbackRenderContext(80, context.Palette);

        Task Draw(IReadOnlyList<ILiveBufferItem> items, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            drawn.Add(items);
            return Task.CompletedTask;
        }

        Task Commit(IScrollbackItem item, IReadOnlyList<ILiveBufferItem> items, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            committed.Add(string.Join('|', item.Render(scrollbackContext)));
            drawn.Add(items);
            return Task.CompletedTask;
        }

        await using var view = new RawActivityView(
            Draw,
            Commit,
            new ToolPresenterRegistry([], new GenericToolPresenter()),
            static (_, _) => Task.CompletedTask);
        await view.ReplaceContent([new SpinnerValue("existing content", 0)], cancellationToken);
        await view.Render(
            new Event
            {
                AgentSessionId = "root",
                TurnStarted = new TurnStarted
                {
                    Model = "root-model",
                    ModelAliasIcon = new TurnModelAliasIcon
                    {
                        Glyph = "R",
                        Color = TurnModelAliasIconColor.Yellow,
                    },
                },
            },
            cancellationToken);
        await view.Render(
            new Event
            {
                AgentSessionId = "child",
                AgentStarted = new AgentStarted { ParentAgentSessionId = "root", Name = "worker" },
            },
            cancellationToken);
        await view.Render(
            new Event
            {
                AgentSessionId = "child",
                TurnStarted = new TurnStarted
                {
                    Model = "child-model",
                    ModelAliasIcon = new TurnModelAliasIcon
                    {
                        Glyph = "◆",
                        Color = TurnModelAliasIconColor.Red,
                    },
                },
            },
            cancellationToken);

        var spinner = Render(drawn[^1], context);
        var spinnerRow = spinner.Split('|').Single(static row => row.Contains("agent worker", StringComparison.Ordinal));
        _ = await Assert.That(spinnerRow).IsEqualTo("  ⠋ [worker] ◆ agent worker");
        _ = await Assert.That(Count(spinner, "◆")).IsEqualTo(1);
        _ = await Assert.That(spinner).DoesNotContain("R");
        _ = await Assert.That(spinner).Contains("existing content");

        await view.Render(
            new Event
            {
                AgentSessionId = "child",
                ToolStarted = new ToolStarted { ToolCallId = "tool", ToolName = "read" },
            },
            cancellationToken);
        var withTool = Render(drawn[^1], context);
        _ = await Assert.That(withTool).Contains("read");
        _ = await Assert.That(Count(withTool, "◆")).IsEqualTo(1);

        await view.Render(
            new Event { AgentSessionId = "child", TextChunk = new TextChunk { Fragment = "first response" } },
            cancellationToken);
        var response = Render(drawn[^1], context);
        var responseRow = response.Split('|').Single(static row => row.Contains("first response", StringComparison.Ordinal));
        _ = await Assert.That(responseRow).IsEqualTo("  ● [worker] ◆ first response");
        _ = await Assert.That(Count(response, "◆")).IsEqualTo(1);

        await view.Render(
            new Event { AgentSessionId = "child", TurnEnded = new TurnEnded { FinishReason = "stop" } },
            cancellationToken);
        _ = await Assert.That(string.Join('|', committed)).DoesNotContain("◆");
        _ = await Assert.That(string.Join('|', committed)).DoesNotContain("R");

        await view.Render(
            new Event
            {
                AgentSessionId = "child",
                TurnStarted = new TurnStarted
                {
                    Model = "next-model",
                    ModelAliasIcon = new TurnModelAliasIcon
                    {
                        Glyph = "◇",
                        Color = TurnModelAliasIconColor.Blue,
                    },
                },
            },
            cancellationToken);
        await view.Render(
            new Event
            {
                AgentSessionId = "child",
                TurnStarted = new TurnStarted
                {
                    Model = "duplicate",
                    ModelAliasIcon = new TurnModelAliasIcon
                    {
                        Glyph = "△",
                        Color = TurnModelAliasIconColor.Green,
                    },
                },
            },
            cancellationToken);
        var nextSpinner = Render(drawn[^1], context);
        _ = await Assert.That(nextSpinner).Contains("[worker] ◇ agent worker");
        _ = await Assert.That(nextSpinner).DoesNotContain("◆");
        _ = await Assert.That(nextSpinner).DoesNotContain("△");

        await view.Render(
            new Event { AgentSessionId = "child", TextChunk = new TextChunk { Fragment = "replacement" } },
            cancellationToken);
        var nextResponse = Render(drawn[^1], context);
        _ = await Assert.That(nextResponse).Contains("[worker] ◇ replacement");
        _ = await Assert.That(nextResponse).DoesNotContain("first response");

        await view.Render(
            new Event { AgentSessionId = "child", TurnEnded = new TurnEnded { FinishReason = "stop" } },
            cancellationToken);
        await view.Render(
            new Event { AgentSessionId = "child", TurnStarted = new TurnStarted { Model = "unaliased" } },
            cancellationToken);
        var clearedSpinner = Render(drawn[^1], context);
        _ = await Assert.That(clearedSpinner).Contains("[worker] agent worker");
        _ = await Assert.That(clearedSpinner).DoesNotContain("◇");
        _ = await Assert.That(clearedSpinner).DoesNotContain("replacement");
    }

    [Test]
    [Arguments(18, "worker")]
    [Arguments(18, "界界")]
    [Arguments(8, "a-very-long-agent-label")]
    public async Task Hierarchical_tool_rows_fit_columns(int columns, string label)
    {
        var palette = new TerminalPalette(false);
        ILiveBufferItem liveItem = new HierarchicalLiveValue(new ToolLiveValue("$ alpha beta", [], 0), 1, label, null);
        var live = liveItem.Render(new LiveBufferRenderContext(columns, palette));
        IScrollbackItem scrollbackItem = new HierarchicalScrollbackValue(
            new ToolScrollbackValue("$ alpha beta", ["output value"], ToolTerminalStatus.Succeeded),
            1,
            label);
        var scrollback = scrollbackItem.Render(new ScrollbackRenderContext(columns, palette));

        _ = await Assert.That(live.Lines.All(line => TerminalText.Width(line.Text) <= columns)).IsTrue();
        _ = await Assert.That(scrollback.All(line => TerminalText.Width(line) <= columns)).IsTrue();
        if (columns == 18 && label == "worker")
        {
            _ = await Assert.That(string.Join('|', live.Lines.Select(line => line.Text))).Contains("$ a");
        }
    }

    [Test]
    public async Task Child_model_alias_icon_styles_only_the_glyph_and_reserves_its_cell_width()
    {
        var palette = new TerminalPalette(true);
        ILiveBufferItem value = new HierarchicalLiveValue(
            new StreamedResponseValue(TerminalIcons.AssistantMessage, "12345678901234567890"),
            1,
            "界 worker",
            new LiveModelAliasIcon("界", ModelAliasIconColor.Gray));

        var rendered = value.Render(new LiveBufferRenderContext(20, palette));
        var line = rendered.Lines.Single();
        var span = line.StyleSpans.Single();
        var labelEnd = line.Text.IndexOf("] ", StringComparison.Ordinal) + 2;
        var glyphStart = line.Text.IndexOf('界', labelEnd);

        _ = await Assert.That(line.Text).IsEqualTo("  ● [界 worker] 界 0");
        _ = await Assert.That(TerminalText.Width(line.Text)).IsLessThanOrEqualTo(20);
        _ = await Assert.That(span.StartCell).IsEqualTo(TerminalText.Width(line.Text[..glyphStart]));
        _ = await Assert.That(span.Length).IsEqualTo(2);
        _ = await Assert.That(span.Style).IsEqualTo(palette.GetLiveIconStyle(ModelAliasIconColor.Gray));
        _ = await Assert.That(span.Style.Start).IsEqualTo("\u001b[48;5;236m\u001b[38;5;245m");
    }

    [Test]
    public async Task Queue_inventory_orders_owner_branches_and_keeps_root_rows_flat(
        CancellationToken cancellationToken)
    {
        var draws = new List<string>();
        var context = new LiveBufferRenderContext(120, new TerminalPalette(false));
        await using var view = new RawActivityView(
            (items, token) =>
            {
                token.ThrowIfCancellationRequested();
                draws.Add(Render(items, context));
                return Task.CompletedTask;
            },
            static (_, _, token) =>
            {
                token.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            },
            new ToolPresenterRegistry([], new GenericToolPresenter()),
            static (_, _) => Task.CompletedTask);
        QueueState[] queues =
            [
                new QueueState
                {
                    OwnerAgentSessionId = "root",
                    OwnerAgentName = "main",
                    ParentAgentSessionId = string.Empty,
                    ParentAgentName = string.Empty,
                    Name = "root-q",
                    Description = "root queue",
                    ItemCount = 1,
                },
                new QueueState
                {
                    OwnerAgentSessionId = "z",
                    OwnerAgentName = "z-parent",
                    ParentAgentSessionId = "root",
                    ParentAgentName = "main",
                    Name = "work",
                    Description = "z queue",
                    ItemCount = 1,
                },
                new QueueState
                {
                    OwnerAgentSessionId = "child",
                    OwnerAgentName = "child",
                    ParentAgentSessionId = "z",
                    ParentAgentName = "z-parent",
                    Name = "work",
                    Description = "child queue",
                    ItemCount = 1,
                },
                new QueueState
                {
                    OwnerAgentSessionId = "a",
                    OwnerAgentName = "a-sibling",
                    ParentAgentSessionId = "root",
                    ParentAgentName = "main",
                    Name = "work",
                    Description = "a queue",
                    ItemCount = 1,
                },
            ];
        foreach (var queue in queues)
        {
            await view.ReplaceQueues(
            new QueueSnapshot
            {
                RootAgentSessionId = "root",
                OwnerAgentSessionId = queue.OwnerAgentSessionId,
                InventoryInstanceId = queue.OwnerAgentSessionId,
                Revision = 1,
                Queues = { queue },
            },
            cancellationToken);
        }

        var rendered = draws[^1];
        var a = rendered.IndexOf("[a-sibling]", StringComparison.Ordinal);
        var child = rendered.IndexOf("[child]", StringComparison.Ordinal);
        var z = rendered.IndexOf("[z-parent]", StringComparison.Ordinal);
        var root = rendered.IndexOf("queue: root-q", StringComparison.Ordinal);
        _ = await Assert.That(a).IsGreaterThanOrEqualTo(0);
        _ = await Assert.That(child).IsGreaterThan(a);
        _ = await Assert.That(z).IsGreaterThan(child);
        _ = await Assert.That(root).IsGreaterThan(z);
        _ = await Assert.That(Count(rendered, "queue: work")).IsEqualTo(3);
        _ = await Assert.That(rendered).DoesNotContain("[root]");
        _ = await Assert.That(Count(rendered, "agent child")).IsEqualTo(1);
        _ = await Assert.That(Count(rendered, "agent z-parent")).IsEqualTo(1);

        await view.ReplaceQueues(
            new QueueSnapshot
            {
                RootAgentSessionId = "root",
                OwnerAgentSessionId = "child",
                InventoryInstanceId = "child",
                Revision = 2,
                Removed = true,
            },
            cancellationToken);
        await view.ReplaceQueues(
            new QueueSnapshot
            {
                RootAgentSessionId = "root",
                OwnerAgentSessionId = "child",
                InventoryInstanceId = "child",
                Revision = 3,
                Queues = { queues[2] },
            },
            cancellationToken);
        _ = await Assert.That(draws[^1]).DoesNotContain("child queue");
        _ = await Assert.That(draws[^1]).Contains("root queue");
        _ = await Assert.That(draws[^1]).Contains("a queue");
        _ = await Assert.That(draws[^1]).Contains("z queue");

        await view.ReplaceQueues(
            new QueueSnapshot
            {
                RootAgentSessionId = "root",
                OwnerAgentSessionId = "z",
                InventoryInstanceId = "replacement",
                Revision = 1,
            },
            cancellationToken);
        await view.ReplaceQueues(
            new QueueSnapshot
            {
                RootAgentSessionId = "root",
                OwnerAgentSessionId = "z",
                InventoryInstanceId = "z",
                Revision = 2,
                Queues = { queues[1] },
            },
            cancellationToken);
        _ = await Assert.That(draws[^1]).DoesNotContain("z queue");
        _ = await Assert.That(draws[^1]).Contains("root queue");
        _ = await Assert.That(draws[^1]).Contains("a queue");
    }

    [Test]
    public async Task Queue_inventory_identifies_root_when_attaching_after_turn_started(
        CancellationToken cancellationToken)
    {
        var draws = new List<string>();
        var context = new LiveBufferRenderContext(120, new TerminalPalette(false));
        await using var view = new RawActivityView(
            (items, _) =>
            {
                draws.Add(Render(items, context));
                return Task.CompletedTask;
            },
            static (_, _, _) => Task.CompletedTask,
            new ToolPresenterRegistry([], new GenericToolPresenter()),
            static (_, _) => Task.CompletedTask);

        await view.ReplaceQueues(
            new QueueSnapshot
            {
                RootAgentSessionId = "opaque-root",
                OwnerAgentSessionId = "opaque-root",
                InventoryInstanceId = "inventory",
                Revision = 1,
                Queues =
                {
                    new QueueState
                    {
                        OwnerAgentSessionId = "opaque-root",
                        OwnerAgentName = "main",
                        ParentAgentSessionId = string.Empty,
                        ParentAgentName = string.Empty,
                        Name = "work",
                        Description = "root queue",
                        ItemCount = 1,
                    },
                },
            },
            cancellationToken);

        _ = await Assert.That(draws[^1]).IsEqualTo("  queue: work · 1 item — root queue");
    }

    [Test]
    public async Task Agent_send_resolves_the_recipient_name_in_live_and_completed_activity(
        CancellationToken cancellationToken)
    {
        var drawn = new List<string>();
        var committed = new List<string>();
        var liveContext = new LiveBufferRenderContext(120, new TerminalPalette(false));
        var scrollbackContext = new ScrollbackRenderContext(120, liveContext.Palette);
        await using var view = new RawActivityView(
            (items, token) =>
            {
                token.ThrowIfCancellationRequested();
                drawn.Add(Render(items, liveContext));
                return Task.CompletedTask;
            },
            (item, _, token) =>
            {
                token.ThrowIfCancellationRequested();
                committed.Add(string.Join('|', item.Render(scrollbackContext)));
                return Task.CompletedTask;
            },
            new ToolPresenterRegistry([new AgentSendToolPresenter()], new GenericToolPresenter()),
            static (_, _) => Task.CompletedTask);

        await view.Render(
            new Event { AgentSessionId = "root", TurnStarted = new TurnStarted { Model = "model" } },
            cancellationToken);
        await view.Render(
            new Event
            {
                AgentSessionId = "root",
                ToolCallChunk = new ToolCallChunk
                {
                    ToolCallId = "send",
                    ToolName = "agent_send",
                    ArgumentsFragment = "{\"name\":\"agent-session-opaque\",\"message\":\"inspect logs\"}",
                },
            },
            cancellationToken);
        await view.Render(
            new Event
            {
                AgentSessionId = "root",
                ToolStarted = new ToolStarted { ToolCallId = "send", ToolName = "agent_send" },
            },
            cancellationToken);

        _ = await Assert.That(drawn[^1]).Contains("Send to agent-session-opaque");

        await view.Render(
            new Event
            {
                AgentSessionId = "agent-session-opaque",
                AgentStarted = new AgentStarted { ParentAgentSessionId = "root", Name = "scout" },
            },
            cancellationToken);

        _ = await Assert.That(drawn[^1]).Contains("Send to scout");
        _ = await Assert.That(drawn[^1]).DoesNotContain("Send to agent-session-opaque");

        await view.Render(
            new Event
            {
                AgentSessionId = "root",
                ToolFinished = new ToolFinished
                {
                    ToolCallId = "send",
                    ToolName = "agent_send",
                    Result = "{\"name\":\"scout\",\"status\":\"running\"}",
                },
            },
            cancellationToken);

        _ = await Assert.That(committed).HasSingleItem();
        _ = await Assert.That(committed[0]).Contains("✓ Send to scout|  inspect logs");
        _ = await Assert.That(committed[0]).DoesNotContain("agent-session-opaque");
    }

    [Test]
    public async Task Agent_send_refreshes_a_root_recipient_after_an_empty_queue_snapshot(
        CancellationToken cancellationToken)
    {
        var drawn = new List<string>();
        var context = new LiveBufferRenderContext(120, new TerminalPalette(false));
        await using var view = new RawActivityView(
            (items, token) =>
            {
                token.ThrowIfCancellationRequested();
                drawn.Add(Render(items, context));
                return Task.CompletedTask;
            },
            static (_, _, token) =>
            {
                token.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            },
            new ToolPresenterRegistry([new AgentSendToolPresenter()], new GenericToolPresenter()),
            static (_, _) => Task.CompletedTask);

        await view.Render(
            new Event
            {
                AgentSessionId = "child",
                ToolCallChunk = new ToolCallChunk
                {
                    ToolCallId = "send",
                    ToolName = "agent_send",
                    ArgumentsFragment = "{\"name\":\"late-root\",\"message\":\"completed\"}",
                },
            },
            cancellationToken);
        await view.Render(
            new Event
            {
                AgentSessionId = "child",
                ToolStarted = new ToolStarted { ToolCallId = "send", ToolName = "agent_send" },
            },
            cancellationToken);

        _ = await Assert.That(drawn[^1]).Contains("Send to late-root");

        await view.ReplaceQueues(
            new QueueSnapshot { RootAgentSessionId = "late-root", OwnerAgentSessionId = "late-root", InventoryInstanceId = "inventory", Revision = 1 },
            cancellationToken);

        _ = await Assert.That(drawn[^1]).Contains("Send to main");
        _ = await Assert.That(drawn[^1]).DoesNotContain("Send to late-root");
    }

    [Test]
    public async Task Compaction_lifecycle_renders_live_activity_and_terminal_status(
        CancellationToken cancellationToken)
    {
        var drawn = new List<string>();
        var committed = new List<string>();
        var liveContext = new LiveBufferRenderContext(120, new TerminalPalette(false));
        var scrollbackContext = new ScrollbackRenderContext(120, liveContext.Palette);

        Task Draw(IReadOnlyList<ILiveBufferItem> items, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            drawn.Add(Render(items, liveContext));
            return Task.CompletedTask;
        }

        Task Commit(IScrollbackItem item, IReadOnlyList<ILiveBufferItem> items, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            _ = items;
            committed.Add(string.Join('|', item.Render(scrollbackContext)));
            return Task.CompletedTask;
        }

        await using var view = new RawActivityView(
            Draw,
            Commit,
            new ToolPresenterRegistry([], new GenericToolPresenter()),
            static (_, _) => Task.CompletedTask);
        await view.Render(
            new Event { AgentSessionId = "root", TurnStarted = new TurnStarted { Model = "model" } },
            cancellationToken);
        await view.Render(
            new Event { AgentSessionId = "root", CompactionStarted = new CompactionStarted() },
            cancellationToken);

        _ = await Assert.That(drawn[^1]).Contains("Compacting…");

        await view.Render(
            new Event { AgentSessionId = "root", CompactionFinished = new CompactionFinished() },
            cancellationToken);
        await view.Render(
            new Event { AgentSessionId = "root", CompactionStarted = new CompactionStarted() },
            cancellationToken);
        await view.Render(
            new Event
            {
                AgentSessionId = "root",
                CompactionFailed = new CompactionFailed { Message = "boom\u001b[2J" },
            },
            cancellationToken);

        _ = await Assert.That(string.Join('|', committed))
            .IsEqualTo("✓ compaction finished|✗ compaction failed: boom[2J");
    }

    [Test]
    public async Task Hierarchy_resolves_depth_orphans_cycles_and_post_order()
    {
        var hierarchy = new AgentSessionHierarchy();
        hierarchy.Observe(new Event
        {
            AgentSessionId = "orphan",
            AgentStarted = new AgentStarted { ParentAgentSessionId = "missing", Name = "orphan" },
        });
        hierarchy.Observe(new Event
        {
            AgentSessionId = "cycle-a",
            AgentStarted = new AgentStarted { ParentAgentSessionId = "cycle-b", Name = "a" },
        });
        hierarchy.Observe(new Event
        {
            AgentSessionId = "cycle-b",
            AgentStarted = new AgentStarted { ParentAgentSessionId = "cycle-a", Name = "b" },
        });
        hierarchy.Observe(new Event { AgentSessionId = "root", TurnStarted = new TurnStarted() });
        hierarchy.Observe(new Event
        {
            AgentSessionId = "child",
            AgentStarted = new AgentStarted { ParentAgentSessionId = "root", Name = "friendly" },
        });

        var order = hierarchy.GetPostOrder(["root", "child", "orphan", "cycle-a", "cycle-b"]);
        _ = await Assert.That(hierarchy.GetDepth("child")).IsEqualTo(1);
        _ = await Assert.That(hierarchy.GetDepth("orphan")).IsEqualTo(1);
        _ = await Assert.That(hierarchy.GetDepth("cycle-a")).IsGreaterThanOrEqualTo(1);
        _ = await Assert.That(hierarchy.GetLabel("child")).IsEqualTo("friendly");
        _ = await Assert.That(hierarchy.ResolveAgentReference("root", "child")).IsEqualTo("friendly");
        _ = await Assert.That(hierarchy.ResolveAgentReference("child", "parent")).IsEqualTo("main");
        _ = await Assert.That(hierarchy.ResolveAgentReference("child", "root")).IsEqualTo("main");
        _ = await Assert.That(hierarchy.ResolveAgentReference("child", "unknown")).IsEqualTo("unknown");
        _ = await Assert.That(order["child"]).IsLessThan(order["root"]);
        _ = await Assert.That(order.Count).IsEqualTo(5);
    }

    [Test]
    public async Task Active_agent_task_progress_debounces_commits_and_preserves_live_hierarchy(
        CancellationToken cancellationToken)
    {
        var drawn = new List<string>();
        var committed = new List<string>();
        var progressDelay = new ControlledProgressDelay();
        var liveContext = new LiveBufferRenderContext(120, new TerminalPalette(false));
        var scrollbackContext = new ScrollbackRenderContext(120, liveContext.Palette);

        Task Draw(IReadOnlyList<ILiveBufferItem> items, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            drawn.Add(Render(items, liveContext));
            return Task.CompletedTask;
        }

        Task Commit(IScrollbackItem item, IReadOnlyList<ILiveBufferItem> items, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            committed.Add(string.Join('|', item.Render(scrollbackContext)));
            drawn.Add(Render(items, liveContext));
            return Task.CompletedTask;
        }

        var presenters = new ToolPresenterRegistry([new RunAgentTasksToolPresenter(new GenericToolPresenter())], new GenericToolPresenter());
        await using var view = new RawActivityView(
            Draw,
            Commit,
            static token => Task.Delay(Timeout.InfiniteTimeSpan, token),
            progressDelay.Delay,
            presenters,
            static (_, _) => Task.CompletedTask);
        await StartChildAgentTask(view, "call", cancellationToken);

        await view.Render(
            new Event
            {
                AgentSessionId = "child",
                AgentTaskProgressSnapshot = new AgentTaskProgressSnapshot
                {
                    OriginToolCallId = "call",
                    Revision = 1,
                    RootNodes =
                    {
                        new AgentTaskProgressNode
                        {
                            Name = "first",
                            Status = AgentTaskProgressStatus.Running,
                            Children =
                            {
                                new AgentTaskProgressNode { Name = "nested", Status = AgentTaskProgressStatus.Pending },
                            },
                        },
                    },
                },
            },
            cancellationToken);
        _ = await Assert.That(drawn[^1]).Contains("◐ first");
        await view.Render(
            new Event
            {
                AgentSessionId = "child",
                AgentTaskProgressSnapshot = new AgentTaskProgressSnapshot
                {
                    OriginToolCallId = "call",
                    Revision = 2,
                    RootNodes =
                    {
                        new AgentTaskProgressNode
                        {
                            Name = "higher",
                            Status = AgentTaskProgressStatus.Running,
                            Children =
                            {
                                new AgentTaskProgressNode { Name = "nested", Status = AgentTaskProgressStatus.Pending },
                            },
                        },
                    },
                },
            },
            cancellationToken);
        _ = await Assert.That(drawn[^1]).Contains("◐ higher");
        _ = await Assert.That(drawn[^1]).DoesNotContain("◐ first");
        _ = await Assert.That(progressDelay.Count).IsEqualTo(2);

        var drawCount = drawn.Count;
        await view.Render(
            new Event
            {
                AgentSessionId = "child",
                AgentTaskProgressSnapshot = new AgentTaskProgressSnapshot
                {
                    OriginToolCallId = "call",
                    Revision = 1,
                    RootNodes =
                    {
                        new AgentTaskProgressNode
                        {
                            Name = "stale",
                            Status = AgentTaskProgressStatus.Running,
                            Children =
                            {
                                new AgentTaskProgressNode { Name = "nested", Status = AgentTaskProgressStatus.Pending },
                            },
                        },
                    },
                },
            },
            cancellationToken);
        await view.Render(
            new Event
            {
                AgentSessionId = "child",
                AgentTaskProgressSnapshot = new AgentTaskProgressSnapshot
                {
                    OriginToolCallId = "wrong",
                    Revision = 99,
                    RootNodes =
                    {
                        new AgentTaskProgressNode
                        {
                            Name = "wrong",
                            Status = AgentTaskProgressStatus.Running,
                            Children =
                            {
                                new AgentTaskProgressNode { Name = "nested", Status = AgentTaskProgressStatus.Pending },
                            },
                        },
                    },
                },
            },
            cancellationToken);
        _ = await Assert.That(drawn).Count().IsEqualTo(drawCount);
        _ = await Assert.That(progressDelay.Count).IsEqualTo(2);

        progressDelay.Release(1);
        await WaitForCount(committed, 1, cancellationToken);
        _ = await Assert.That(committed[0]).Contains("  • [worker] Agent tasks:|    [worker] ◐ higher|    [worker] └── ○ nested");
        _ = await Assert.That(drawn[^1]).Contains("◐ higher");

        await view.Render(
            new Event
            {
                AgentSessionId = "child",
                AgentTaskProgressSnapshot = new AgentTaskProgressSnapshot
                {
                    OriginToolCallId = "call",
                    Revision = 3,
                    RootNodes =
                    {
                        new AgentTaskProgressNode
                        {
                            Name = "newer",
                            Status = AgentTaskProgressStatus.Running,
                            Children =
                            {
                                new AgentTaskProgressNode { Name = "nested", Status = AgentTaskProgressStatus.Pending },
                            },
                        },
                    },
                },
            },
            cancellationToken);
        progressDelay.Release(2);
        await WaitForCount(committed, 2, cancellationToken);
        _ = await Assert.That(committed[1]).Contains("◐ newer");

        await view.Render(
            new Event
            {
                AgentSessionId = "child",
                ToolFinished = new ToolFinished { ToolCallId = "call", ToolName = "run_agent_tasks" },
            },
            cancellationToken);
        _ = await Assert.That(committed).Count().IsEqualTo(3);
        _ = await Assert.That(committed[2]).DoesNotContain("Agent tasks:");
        _ = await Assert.That(drawn[^1]).Contains("Agent tasks:");
        await view.Render(
            new Event
            {
                AgentSessionId = "child",
                AgentTaskProgressSnapshot = new AgentTaskProgressSnapshot
                {
                    OriginToolCallId = "call",
                    Revision = 4,
                    RootNodes =
                    {
                        new AgentTaskProgressNode
                        {
                            Name = "late",
                            Status = AgentTaskProgressStatus.Running,
                            Children =
                            {
                                new AgentTaskProgressNode { Name = "nested", Status = AgentTaskProgressStatus.Pending },
                            },
                        },
                    },
                },
            },
            cancellationToken);
        _ = await Assert.That(progressDelay.Count).IsEqualTo(4);
        progressDelay.Release(3);
        await WaitForCount(committed, 4, cancellationToken);
        _ = await Assert.That(committed[^1]).Contains("late");
        _ = await Assert.That(string.Join('|', committed)).DoesNotContain("stale");
        _ = await Assert.That(string.Join('|', committed)).DoesNotContain("wrong");
    }

    [Test]
    [Arguments(Event.PayloadOneofCase.ToolFinished)]
    [Arguments(Event.PayloadOneofCase.ToolCancelled)]
    [Arguments(Event.PayloadOneofCase.ToolError)]
    public async Task Tool_terminal_flushes_pending_progress_before_terminal_output(
        Event.PayloadOneofCase terminalCase,
        CancellationToken cancellationToken)
    {
        var committed = new List<string>();
        var context = new ScrollbackRenderContext(120, new TerminalPalette(false));

        Task Commit(IScrollbackItem item, IReadOnlyList<ILiveBufferItem> items, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            _ = items;
            committed.Add(string.Join('|', item.Render(context)));
            return Task.CompletedTask;
        }

        await using var view = new RawActivityView(
            static (_, _) => Task.CompletedTask,
            Commit,
            static token => Task.Delay(Timeout.InfiniteTimeSpan, token),
            static (_, token) => Task.Delay(Timeout.InfiniteTimeSpan, token),
            new ToolPresenterRegistry([new RunAgentTasksToolPresenter(new GenericToolPresenter())], new GenericToolPresenter()),
            static (_, _) => Task.CompletedTask);
        await StartChildAgentTask(view, "call", cancellationToken);
        await view.Render(
            new Event
            {
                AgentSessionId = "child",
                AgentTaskProgressSnapshot = new AgentTaskProgressSnapshot
                {
                    OriginToolCallId = "call",
                    Revision = 1,
                    RootNodes =
                    {
                        new AgentTaskProgressNode
                        {
                            Name = "pending",
                            Status = AgentTaskProgressStatus.Running,
                            Children =
                            {
                                new AgentTaskProgressNode { Name = "nested", Status = AgentTaskProgressStatus.Pending },
                            },
                        },
                    },
                },
            },
            cancellationToken);
        await view.Render(TerminalEvent(terminalCase), cancellationToken);

        _ = await Assert.That(committed).Count().IsEqualTo(2);
        _ = await Assert.That(committed[0]).Contains("Agent tasks:");
        _ = await Assert.That(committed[0]).Contains("pending");
        _ = await Assert.That(committed[1]).DoesNotContain("Agent tasks:");
    }

    [Test]
    public async Task Agent_task_progress_calls_debounce_independently_and_shutdown_flushes_pending(
        CancellationToken cancellationToken)
    {
        var committed = new List<string>();
        var progressDelay = new ControlledProgressDelay();
        var context = new ScrollbackRenderContext(120, new TerminalPalette(false));

        Task Commit(IScrollbackItem item, IReadOnlyList<ILiveBufferItem> items, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            _ = items;
            committed.Add(string.Join('|', item.Render(context)));
            return Task.CompletedTask;
        }

        await using var view = new RawActivityView(
            static (_, _) => Task.CompletedTask,
            Commit,
            static token => Task.Delay(Timeout.InfiniteTimeSpan, token),
            progressDelay.Delay,
            new ToolPresenterRegistry([new RunAgentTasksToolPresenter(new GenericToolPresenter())], new GenericToolPresenter()),
            static (_, _) => Task.CompletedTask);
        await StartChildAgentTask(view, "first-call", cancellationToken);
        await StartChildAgentTask(view, "second-call", cancellationToken);
        await view.Render(
            new Event
            {
                AgentSessionId = "child",
                AgentTaskProgressSnapshot = new AgentTaskProgressSnapshot
                {
                    OriginToolCallId = "first-call",
                    Revision = 1,
                    RootNodes =
                    {
                        new AgentTaskProgressNode
                        {
                            Name = "first-call-tree",
                            Status = AgentTaskProgressStatus.Running,
                            Children =
                            {
                                new AgentTaskProgressNode { Name = "nested", Status = AgentTaskProgressStatus.Pending },
                            },
                        },
                    },
                },
            },
            cancellationToken);
        await view.Render(
            new Event
            {
                AgentSessionId = "child",
                AgentTaskProgressSnapshot = new AgentTaskProgressSnapshot
                {
                    OriginToolCallId = "second-call",
                    Revision = 1,
                    RootNodes =
                    {
                        new AgentTaskProgressNode
                        {
                            Name = "second-call-tree",
                            Status = AgentTaskProgressStatus.Running,
                            Children =
                            {
                                new AgentTaskProgressNode { Name = "nested", Status = AgentTaskProgressStatus.Pending },
                            },
                        },
                    },
                },
            },
            cancellationToken);

        progressDelay.Release(0);
        await WaitForCount(committed, 1, cancellationToken);
        _ = await Assert.That(committed[0]).Contains("first-call-tree");
        _ = await Assert.That(committed[0]).DoesNotContain("second-call-tree");

        await view.Shutdown();
        _ = await Assert.That(committed).Count().IsEqualTo(2);
        _ = await Assert.That(committed[1]).Contains("second-call-tree");
        _ = await Assert.That(progressDelay.CancelledCount).IsGreaterThanOrEqualTo(1);
    }

    [Test]
    public async Task Background_progress_commit_failure_remains_retryable_and_is_surfaced_by_shutdown(
        CancellationToken cancellationToken)
    {
        var commitAttempts = 0;
        var successfulCommits = 0;
        var firstAttempt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var progressDelay = new ControlledProgressDelay();

        Task Commit(IScrollbackItem item, IReadOnlyList<ILiveBufferItem> items, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            _ = item;
            _ = items;
            if (Interlocked.Increment(ref commitAttempts) == 1)
            {
                _ = firstAttempt.TrySetResult();
                throw new InvalidOperationException("progress commit failed");
            }

            _ = Interlocked.Increment(ref successfulCommits);
            return Task.CompletedTask;
        }

        await using var view = new RawActivityView(
            static (_, _) => Task.CompletedTask,
            Commit,
            static token => Task.Delay(Timeout.InfiniteTimeSpan, token),
            progressDelay.Delay,
            new ToolPresenterRegistry([new RunAgentTasksToolPresenter(new GenericToolPresenter())], new GenericToolPresenter()),
            static (_, _) => Task.CompletedTask);
        await StartChildAgentTask(view, "call", cancellationToken);
        await view.Render(
            new Event
            {
                AgentSessionId = "child",
                AgentTaskProgressSnapshot = new AgentTaskProgressSnapshot
                {
                    OriginToolCallId = "call",
                    Revision = 1,
                    RootNodes =
                    {
                        new AgentTaskProgressNode
                        {
                            Name = "pending",
                            Status = AgentTaskProgressStatus.Running,
                            Children =
                            {
                                new AgentTaskProgressNode { Name = "nested", Status = AgentTaskProgressStatus.Pending },
                            },
                        },
                    },
                },
            },
            cancellationToken);

        progressDelay.Release(0);
        await firstAttempt.Task.WaitAsync(cancellationToken);

        _ = await Assert.That(view.Shutdown).Throws<InvalidOperationException>();
        _ = await Assert.That(commitAttempts).IsEqualTo(2);
        _ = await Assert.That(successfulCommits).IsEqualTo(1);
        _ = await Assert.That(view.Shutdown).Throws<InvalidOperationException>();
        _ = await Assert.That(commitAttempts).IsEqualTo(2);
    }

    [Test]
    public async Task Disposal_after_quiet_progress_flush_does_not_duplicate_the_commit(
        CancellationToken cancellationToken)
    {
        var committed = new List<string>();
        var progressDelay = new ControlledProgressDelay();

        Task Commit(IScrollbackItem item, IReadOnlyList<ILiveBufferItem> items, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            _ = item;
            _ = items;
            committed.Add("committed");
            return Task.CompletedTask;
        }

        var view = new RawActivityView(
            static (_, _) => Task.CompletedTask,
            Commit,
            static token => Task.Delay(Timeout.InfiniteTimeSpan, token),
            progressDelay.Delay,
            new ToolPresenterRegistry([new RunAgentTasksToolPresenter(new GenericToolPresenter())], new GenericToolPresenter()),
            static (_, _) => Task.CompletedTask);
        await StartChildAgentTask(view, "call", cancellationToken);
        await view.Render(
            new Event
            {
                AgentSessionId = "child",
                AgentTaskProgressSnapshot = new AgentTaskProgressSnapshot
                {
                    OriginToolCallId = "call",
                    Revision = 1,
                    RootNodes =
                    {
                        new AgentTaskProgressNode
                        {
                            Name = "pending",
                            Status = AgentTaskProgressStatus.Running,
                            Children =
                            {
                                new AgentTaskProgressNode { Name = "nested", Status = AgentTaskProgressStatus.Pending },
                            },
                        },
                    },
                },
            },
            cancellationToken);

        progressDelay.Release(0);
        await WaitForCount(committed, 1, cancellationToken);
        await view.DisposeAsync();
        await view.DisposeAsync();

        _ = await Assert.That(committed).HasSingleItem();
    }

    private static async Task StartChildAgentTask(
        RawActivityView view,
        string callId,
        CancellationToken cancellationToken)
    {
        await view.Render(new Event { AgentSessionId = "root", TurnStarted = new TurnStarted { Model = "model" } }, cancellationToken);
        await view.Render(
            new Event
            {
                AgentSessionId = "child",
                AgentStarted = new AgentStarted { ParentAgentSessionId = "root", Name = "worker" },
            },
            cancellationToken);
        await view.Render(new Event { AgentSessionId = "child", TurnStarted = new TurnStarted { Model = "model" } }, cancellationToken);
        await view.Render(
            new Event
            {
                AgentSessionId = "child",
                ToolCallChunk = new ToolCallChunk { ToolCallId = callId, ToolName = "run_agent_tasks" },
            },
            cancellationToken);
        await view.Render(
            new Event
            {
                AgentSessionId = "child",
                ToolStarted = new ToolStarted { ToolCallId = callId, ToolName = "run_agent_tasks" },
            },
            cancellationToken);
    }

    private static Event TerminalEvent(Event.PayloadOneofCase terminalCase) => terminalCase switch
    {
        Event.PayloadOneofCase.ToolFinished => new Event
        {
            AgentSessionId = "child",
            ToolFinished = new ToolFinished { ToolCallId = "call", ToolName = "run_agent_tasks" },
        },
        Event.PayloadOneofCase.ToolCancelled => new Event
        {
            AgentSessionId = "child",
            ToolCancelled = new ToolCancelled { ToolCallId = "call", ToolName = "run_agent_tasks" },
        },
        Event.PayloadOneofCase.ToolError => new Event
        {
            AgentSessionId = "child",
            ToolError = new ToolError { ToolCallId = "call", ToolName = "run_agent_tasks", Message = "failed" },
        },
        _ => throw new ArgumentOutOfRangeException(nameof(terminalCase)),
    };

    private static async Task WaitForCount<T>(
        List<T> items,
        int count,
        CancellationToken cancellationToken)
    {
        while (items.Count < count)
        {
            await Task.Delay(1, cancellationToken);
        }
    }

    private static string Render(IReadOnlyList<ILiveBufferItem> items, LiveBufferRenderContext context) =>
        string.Join('|', items.SelectMany(item => item.Render(context).Lines).Select(static line => line.Text));

    private static int Count(string value, string fragment) =>
        value.Split(fragment, StringSplitOptions.None).Length - 1;

    private sealed class ControlledProgressDelay
    {
        private readonly List<TaskCompletionSource> _releases = [];
        private int _cancelledCount;

        public int Count => _releases.Count;

        public int CancelledCount => Volatile.Read(ref _cancelledCount);

        public async Task Delay(TimeSpan quietPeriod, CancellationToken cancellationToken)
        {
            _ = quietPeriod;
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _releases.Add(release);
            try
            {
                await release.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _ = Interlocked.Increment(ref _cancelledCount);
                throw;
            }
        }

        public void Release(int index) => _ = _releases[index].TrySetResult();
    }
}
