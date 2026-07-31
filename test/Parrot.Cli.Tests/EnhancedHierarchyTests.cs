using Parrot.Cli.Enhanced;
using Parrot.Cli.Enhanced.Tools;
using Parrot.Llm;
using Parrot.Protocol;

namespace Parrot.Cli.Tests;

internal sealed class EnhancedHierarchyTests
{
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

        using var view = new RawActivityView(
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
        _ = await Assert.That(live).Contains("  ● [child] child");
        var childPosition = live.IndexOf("  ● [child] child", StringComparison.Ordinal);
        _ = await Assert.That(live).DoesNotContain("    ● [child] child");
        _ = await Assert.That(live).Contains("● [parent] parent response");
        var parentPosition = live.IndexOf("● [parent] parent response", StringComparison.Ordinal);
        _ = await Assert.That(live).DoesNotContain("  ● [parent] parent response");
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
        _ = await Assert.That(string.Join('|', committed))
            .IsEqualTo("  ● [parent] parent response|  ♟ [parent] agent finished");
        _ = await Assert.That(drawn[^1]).DoesNotContain("[parent] agent finished");

        await view.Render(
            new Event { AgentSessionId = "child", TurnEnded = new TurnEnded { FinishReason = "stop" } },
            cancellationToken);
        _ = await Assert.That(string.Join('|', committed))
            .IsEqualTo("  ● [parent] parent response|  ♟ [parent] agent finished|    ● [child] child|      [child] response|    ♟ [child] agent finished");
        await view.Render(
            new Event
            {
                AgentSessionId = "child",
                ToolFinished = new ToolFinished { ToolCallId = "tool", ToolName = "read" },
            },
            cancellationToken);
        _ = await Assert.That(string.Join('|', committed))
            .IsEqualTo("  ● [parent] parent response|  ♟ [parent] agent finished|    ● [child] child|      [child] response|    ♟ [child] agent finished|    ✓ [child] tool call read");

        var count = committed.Count;
        await view.Render(
            new Event
            {
                AgentSessionId = "child",
                AgentFinished = new AgentFinished { ParentAgentSessionId = "parent", Name = "child" },
            },
            cancellationToken);
        _ = await Assert.That(committed.Count).IsEqualTo(count);
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

        using var view = new RawActivityView(
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

        _ = await Assert.That(committed).Count().IsEqualTo(2);
        _ = await Assert.That(layouts[0]).IsEqualTo(ScrollbackLayout.Assistant);
        _ = await Assert.That(layouts[1]).IsEqualTo(ScrollbackLayout.Compact);
        _ = await Assert.That(committed[0]).IsEqualTo(
            "  ● [child] Findings|    [child] bold and code|    [child] • first item|" +
            "    [child] public var value = 42;");
        _ = await Assert.That(committed[0]).DoesNotContain("# Findings");
        _ = await Assert.That(committed[0]).DoesNotContain("**");
        _ = await Assert.That(committed[0]).DoesNotContain("```");
        _ = await Assert.That(committed[1]).IsEqualTo("  ♟ [child] agent finished");
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

        using var view = new RawActivityView(
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
        _ = await Assert.That(drawn[^1]).Contains("● [child] line 1 line 2");
        _ = await Assert.That(drawn[^1]).DoesNotContain("  ● [child] line 1 line 2");
        _ = await Assert.That(drawn[^1]).DoesNotContain("|  [child] line 2");
        _ = await Assert.That(drawn[^1]).DoesNotContain("line 11");

        await view.Render(
            new Event { AgentSessionId = "child", TurnEnded = new TurnEnded { FinishReason = "stop" } },
            cancellationToken);

        _ = await Assert.That(committed[0]).IsEqualTo(expectedResponse);
        _ = await Assert.That(string.Join('|', committed)).DoesNotContain("line 11");
        _ = await Assert.That(committed[1]).IsEqualTo("  ♟ [child] agent finished");
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

        using var view = new RawActivityView(
            Draw,
            Commit,
            new ToolPresenterRegistry(
                [new WaitAgentToolPresenter(), new WaitProcessToolPresenter()],
                new GenericToolPresenter()),
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
                AgentSessionId = "child",
                ToolStarted = new ToolStarted { ToolCallId = "agent-wait", ToolName = "wait_agent" },
            },
            cancellationToken);

        _ = await Assert.That(drawn[^1]).Contains("⠋ [worker] agent worker Working: wait_agent");
        _ = await Assert.That(drawn[^1]).DoesNotContain("  ⠋ [worker] agent worker Working: wait_agent");
        _ = await Assert.That(drawn[^1]).DoesNotContain("Wait for");

        await view.Render(
            new Event
            {
                AgentSessionId = "child",
                ToolStarted = new ToolStarted { ToolCallId = "process-wait", ToolName = "wait_process" },
            },
            cancellationToken);

        _ = await Assert.That(drawn[^1]).Contains("Working: wait_process");
        _ = await Assert.That(drawn[^1]).DoesNotContain("wait process-wait");

        await view.Render(
            new Event
            {
                AgentSessionId = "child",
                ToolFinished = new ToolFinished { ToolCallId = "process-wait", ToolName = "wait_process" },
            },
            cancellationToken);

        _ = await Assert.That(drawn[^1]).Contains("Working: wait_agent");
        await view.Render(
            new Event { AgentSessionId = "child", TextChunk = new TextChunk { Fragment = "completed work" } },
            cancellationToken);
        await view.Render(
            new Event { AgentSessionId = "child", TurnEnded = new TurnEnded { FinishReason = "stop" } },
            cancellationToken);

        _ = await Assert.That(string.Join('|', committed))
            .IsEqualTo("  ● [worker] completed work|  ♟ [worker] agent finished");
        await view.Render(
            new Event
            {
                AgentSessionId = "child",
                ToolCancelled = new ToolCancelled { ToolCallId = "agent-wait", ToolName = "wait_agent" },
            },
            cancellationToken);

        _ = await Assert.That(string.Join('|', committed))
            .IsEqualTo("  ● [worker] completed work|  ♟ [worker] agent finished");
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

        using var view = new RawActivityView(
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

        using var view = new RawActivityView(
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

        using var view = new RawActivityView(
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
        _ = await Assert.That(spinner).Contains("⠋ [worker] ◆ agent worker");
        _ = await Assert.That(spinner).DoesNotContain("  ⠋ [worker] ◆ agent worker");
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
        _ = await Assert.That(response).Contains("● [worker] ◆ first response");
        _ = await Assert.That(response).DoesNotContain("  ● [worker] ◆ first response");
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
    public async Task Child_model_alias_icon_styles_only_the_glyph_and_reserves_its_cell_width()
    {
        var palette = new TerminalPalette(true);
        var value = new HierarchicalLiveValue(
            new MarqueeValue("● ", "12345678901234567890", 0),
            1,
            "界 worker",
            "worker",
            "♟",
            new LiveModelAliasIcon("界", ModelAliasIconColor.Gray));

        var rendered = value.Render(new LiveBufferRenderContext(20, palette));
        var line = rendered.Lines.Single();
        var span = line.StyleSpans.Single();
        var labelEnd = line.Text.IndexOf("] ", StringComparison.Ordinal) + 2;
        var glyphStart = line.Text.IndexOf('界', labelEnd);

        _ = await Assert.That(line.Text).StartsWith("  ● [界 worker] 界 ");
        _ = await Assert.That(TerminalText.Width(line.Text)).IsLessThanOrEqualTo(20);
        _ = await Assert.That(span.StartCell).IsEqualTo(TerminalText.Width(line.Text[..glyphStart]));
        _ = await Assert.That(span.Length).IsEqualTo(2);
        _ = await Assert.That(span.Style).IsEqualTo(palette.GetLiveIconStyle(ModelAliasIconColor.Gray));
        _ = await Assert.That(span.Style.Start).IsEqualTo("\u001b[48;5;236m\u001b[38;5;245m");
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
        _ = await Assert.That(order["child"]).IsLessThan(order["root"]);
        _ = await Assert.That(order.Count).IsEqualTo(5);
    }

    private static string Render(IReadOnlyList<ILiveBufferItem> items, LiveBufferRenderContext context) =>
        string.Join('|', items.SelectMany(item => item.Render(context).Lines).Select(static line => line.Text));

    private static int Count(string value, string fragment) =>
        value.Split(fragment, StringSplitOptions.None).Length - 1;
}
