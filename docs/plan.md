# Migration Plan

**Status: M0 through M7 done.** A walking agent: interactive, durable, tools
under a sandbox, context and compaction, subagents, serve, and two renderers. Milestone 0 was the plan gate from
MIGRATION.md §0. `docs/components.md` now has an entry for all 29 blocks and
the level-1 questions are answered in `architecture.md`.

Three rules shape this plan.

**Every milestone ends in a binary you can run and a sentence you can check.**
"Blocks 1 through 4 ported" is not an exit criterion; `parrot chat` returning a
reply is. A milestone that cannot be demonstrated is not done.

**Risk first, not dependency order.** The rank table in
[architecture.md](architecture.md) says what must *exist* before what. It does
not say what to *build* first. Where the two disagree, take the risk: the
expensive failures here are a contract that cannot carry what the CLI needs, a
storage rule discovered wrong after data exists, and a sandbox that fails open.
Each of those is retired as early as it can be.

**Thin before wide.** A milestone takes a block only as far as the milestone
needs. `AgentSession` appears in M1 with one turn and no tools, and is finished
in M5. This is deliberate: a block built to completion before anything uses it
is a block built against guesses.

## Milestones

```text
M0  close the plan gate        no code
M1  walking skeleton           one prompt in, one reply out
M2  durable and recoverable    survives kill -9 and a second machine
M2.5 interactive               a REPL with slash commands
M3  tools and the sandbox      it can edit code, and cannot escape
M4  context and compaction     long conversations stop falling over
M5  agents, tasks, subagents   the full agent loop
M6  serve                      a second machine can drive it
M7  enhanced CLI               the terminal experience
```

---

### M0 — Close the plan gate

**Goal.** `docs/components.md` filled in, one entry per block.

**Exit.** Every block in `architecture.md` has a complete entry, the level-1
questions are answered, and the document has been reviewed. No `<Namespace>`
placeholders left.

**Forces.** The level-1 questions, now answered in the Decisions section of
`architecture.md`.

**No code.** This is the gate, and it is the cheapest place in the project to
change your mind about a boundary.

---

### M1 — Walking skeleton

**Goal.** One prompt in, one reply out, end to end, through the real contract.

**Blocks.** `Configuration`, `StatePaths`, `ICredentialStore`, `ILLMProvider`,
`ProviderRegistry`, `EventBroker`, `AgentSession` (one turn, no tools, no
compaction), `ParrotService`, `InProcessChannel`, `CommandDispatcher`,
`BasicCli`.

**Exit.**

```sh
parrot auth login opencode-go --api-key-stdin
parrot chat --model opencode-go/deepseek-v4-pro
```

`opencode-go` is M1's provider and `deepseek-v4-pro` its default model. Verified
end to end through the AOT binary, through the gRPC contract, over
`InProcessChannel` with no socket bound:

```console
$ parrot chat "Say exactly: hello from parrot dotnet"
hello from parrot dotnet
  turn ended (stop, 91 in / 79 out)
```

On the **glibc-dynamic** AOT publish. The static musl binary builds and runs but
cannot make an HTTPS request; see the unresolved divergence in
`components.md`.

**Retires.** The two risks that would invalidate the most work if found late:

- **Can the `.proto` carry a turn?** Events are flat and self-describing, or
  they are not. `BasicCli` renders them with a `switch` and a `WriteLine`, or
  the event model is wrong. This is why `BasicCli` is in the first milestone
  rather than the last.
- **Does the sink shape hold?** Deltas go to `ILLMEventSink`, the durable
  result is the return value. Cheap to correct here, expensive once five call sites
  depend on it.

**Explicitly not in scope.** Persistence beyond whatever a single process
needs, tools, permissions, subagents, `EnhancedCli`, the socket.

---

### M2 — Durable and recoverable

**Goal.** Sessions survive process death, and two machines cannot corrupt each
other.

**Blocks.** `SessionDatabase`, `EventRepository`, `UserSession`, plus the
durability half of `AgentSession` — admitted input, projected messages, context
epochs.

**Exit.**

```sh
parrot chat            # ... mid-turn ...
kill -9 $(pgrep parrot)
parrot chat            # the session resumes; the interrupted turn is not lost
parrot sessions        # lists sessions, reading meta.json only
```

Plus, verified in tests rather than by eye: no `-shm` or `-wal` file ever
appears under the state directory; a claim on a live binding creates a second
user session instead of stealing the first; a claim on an abandoned one
reclaims it.

**Retires.** The silent-corruption risk. Every rule in the `UserSession`
section is load-bearing and none of them fail loudly — this is the milestone
where getting it wrong is still cheap, because no user has data yet.

**Done.** `journal_mode=TRUNCATE` asserted, no `-shm`/`-wal` under the state
directory, event and projection commit in one transaction, the claim uses a
real `link()` P/Invoke, a live binding is not stolen, an abandoned one resumes
the session it named, and `parrot sessions` lists from `meta.json`.

Not done, and deliberately: an interrupted turn is not *replayed* on resume.
The prompt and the messages are durable and the session is reclaimed, but
recovery does not re-run a turn that died mid-flight. That is a bigger piece of
the drain than M2 needs, and it belongs with the input-promotion work in M5.

---

### M2.5 — Interactive

**Goal.** `parrot chat` opens a session, not a single answer.

**Blocks.** `InteractiveSession`, `ISlashCommand` and eight commands, a renderer
split so one `Listen` stream spans every turn. No new domain block: the contract
already had what a REPL needs, because `Listen` was made indefinite for exactly
this.

**Exit.**

```sh
parrot chat            # a prompt, not an exit
> /auth login          # key entry does not echo
> /model glm-5.2
> hello                # streams
> /clear               # a new session; the old one stays listed
> /exit
echo piped | parrot chat   # still one-shot
parrot chat "argument"     # still one-shot
```

**Why before M3, not with M7.** A permission prompt cannot be answered in a
one-shot process, so `PermissionBroker` is unusable without a loop to answer in.
The eight commands are the ones existing RPCs back; the rest arrive with the
milestone that gives them something to do. This is the basic CLI's loop —
`EnhancedCli` at M7 is a different, richer client, and shares no code with it.

**Done.** Verified through a real PTY: `/version`, `/model` (issues
`UpdateSession`), a streamed turn, `/clear` (new session id, old one still
listed with `*` marking the current), `/nope` reported rather than sent to the
model, `/exit`. One-shot survives both piped and as an argument.

---

### M3 — Tools and the sandbox

**Goal.** It can change code, and it cannot escape.

**Blocks.** `PermissionBroker`, `QuestionBroker`, `ProcessRunner`,
`ToolRegistry`, `ITool` and the builtin tools — `exec_command`, `read`, `glob`,
`grep`, `apply_patch`, `git_diff`, `web_fetch`, `WebFetcher`.

**Exit.** `parrot chat` can be asked to make a change to a file in the working
directory and does it. `exec_command` runs under bubblewrap with the host
filesystem read-only and the workspace writable.

Plus a test that the sandbox **fails closed**: with bubblewrap unavailable, a
shell command does not run. Not a warning, not a fallback. This is the one exit
criterion in the plan that is a security property rather than a feature.

**Retires.** Sandbox escape, and permission semantics that authorise a tool name
rather than an operation (principle 7).

**Done (full built-in tool set; permission prompt deferred).** The agentic loop
runs: `AgentSession` calls the provider, executes the tool calls it returns under
the sandbox, feeds results back, and repeats until the model stops. The complete
built-in inventory is registered: `exec_command`, `read`, `glob`, `grep`,
`apply_patch`, `git_diff`, `web_fetch`, and `agent_spawn`. `exec_command` runs
under bubblewrap with fail-closed asserted in tests. `apply_patch` applies aider
and unified patches directly (no transactional rollback -- a failed apply reports
what it wrote). `WebFetcher` pins DNS-resolved addresses through an
`IWebAddressPolicy` (public-only by default, private opt-in via
`web_fetch.allow_private`). Not yet built: `PermissionBroker`/`QuestionBroker` --
`exec_command` runs sandboxed without a prompt (the sandbox is the boundary), so
the interactive permission flow waits for a tool that needs `disable_sandbox`.
Recorded as an M3 remainder.

**Divergence found:** `deepseek-v4-pro` on opencode-go 400s on tool-continuation
(other models -- glm, kimi, qwen, minimax -- work), so the built-in default
moved to `glm-5.2`. Recorded in components.md.

---

### M4 — Context and compaction

**Goal.** A long conversation stops falling over.

**Blocks.** `SystemContextBuilder`, `Compactor`, and the epoch half of
`AgentSession`.

**Exit.** A conversation driven past the model's context window continues
working: compaction starts a new epoch, and the transcript before the cutoff
stops being sent. `AGENTS.md` files, skills, and tool guidance appear in the
system context, sampled only at a safe turn boundary.

**Done.** `SystemContextBuilder` samples base prompt, date, platform, working
directory, and AGENTS.md files from the cwd upward, once per epoch. AgentSession
now carries a running history across turns -- verified live: it recalled a
number stated in an earlier turn. `Compactor` summarises the older history
through the provider when a token budget is exceeded, keeping the recent tail
verbatim, and starts a fresh epoch. Skills and durable epoch records are
deferred to the milestones that own them.

---

### M5 — Agents, tasks, subagents

**Goal.** The full agent loop, including agents that start agents.

**Blocks.** `TaskManager`, `AgentRegistry` including subagent spawn, and the
rest of `AgentSession` — steer and queue input promotion, interrupt.

**Exit.** `agent_spawn` starts a child session; the child's events appear on the
parent's stream carrying their own `session_id` and `task_id`, and one
subscription still suffices with no correlation table on the client.
Recursion and per-parent concurrency limits hold. A child outlives the turn that
spawned it, and `Await` returns its result.

**Retires.** The `AgentSession`/`AgentRegistry` mutual dependency, which is
inherent but only proven workable once both are real.

**Done (synchronous subagents).** `agent_spawn` delegates a subtask to a child
`AgentSession` that shares the parent's broker and store -- so the child's
events stream on the same session stream -- runs to completion, and returns its
final text as the tool result. Recursion is bounded by a depth limit. Verified
live: the parent spawned a child that replied BANANA and reported it back.

**Divergence:** the child is spawned-and-awaited within the tool call, not run
as a background task the parent `Await`s later. That is the simpler subagent
shape and matches how a delegated-subtask tool usually works; the async
background-session model is deferred with it. Recorded here rather than
pretended complete.

**Done (the message queue: admitted input, promotion, interrupt).** A prompt is
admitted durably against an `input` table and promoted by the drain at the
boundary its delivery names -- every pending steer at each turn boundary, one
queued prompt where the turn would otherwise stop. One drain owns a session, so
a prompt sent during a turn joins it instead of starting a second `Run` on the
same history, which is what used to happen. `Interrupt` stops the turn, settles
every tool call the model had asked for so the next prompt is not rejected for
an unanswered one, and resumes for anything still pending. The CLI reads and
renders at the same time, so a line can be typed mid-turn, and Ctrl-C stops the
turn before it stops parrot.

Three defects went with it: the turn ran on the gRPC call's cancellation token,
`SendMessageResponse.message_id` was a fabricated id, and `/model` rebuilt the
main agent session and lost its history. Divergences are in components.md.

**Remaining in M5:** `TaskManager`, `AgentRegistry` as a block, per-parent
concurrency limits, and replaying pending input at startup.

---

### M6 — Serve

**Goal.** A second machine can drive it.

**Blocks.** `GrpcServer`, `ParrotApplication` as the full composition root.

**Exit.** `parrot serve` binds; a client on another machine drives a session
through the same service the local CLI uses. Local mode still opens no socket.

**Retires.** Principle 11 — that the local and remote paths are genuinely one
contract, and not two that merely resemble each other. Deliberately late: if M1
got the contract right, this milestone is small, and if it did not, this is
where that shows.

---

### M7 — Enhanced CLI

**Goal.** The terminal experience.

**Blocks.** `EnhancedCli`, the `terminal` layer — editor, picker, markdown,
raw mode.

**Exit.** Scrollback-preserving chat, no alternate screen. `BasicCli` still
works and still shares no code with it.

**Last on purpose.** It is the largest block and the one that renders everything
else, so its shape is guessable only once everything it renders exists. It is
also the only milestone that can be cut without cutting the product.

**Done (a first enhanced renderer).** `EnhancedCli` reads the typed payload:
reasoning dim, tool calls announced, the turn summary set apart in colour, no
alternate screen so scrollback is the history. It is the default in a terminal;
`--basic`, or redirected output, selects `BasicCli`. The two share no rendering
-- only the `ITurnRenderer` seam and the generated client -- so BasicCli stays a
pure test of the event contract. Verified live through a PTY: the same turn
renders coloured under the default and plain under `--basic`. The full terminal
editor/picker/markdown layer is a richer M7 than this; what ships is the honest
first version of a distinct enhanced client.

## What this plan does not schedule

Cross-cutting, done continuously rather than at a milestone: porting upstream
tests alongside each block (MIGRATION.md §6), keeping the publish AOT-clean
(§2), and recording every divergence in `components.md` (§1).

`PARROT0004`, the analyzer keeping the two CLIs apart, lands with M7 when there
is finally something to keep apart.
