# Component Map

**Status: written.** This is the Phase 0 deliverable described in MIGRATION.md
§0 — the gate. A component not named here may not be ported.

The level-1 view — which blocks exist, how they connect, their rank, and the
decisions already taken — is in [architecture.md](architecture.md). This file is
level 2: one entry per block. [plan.md](plan.md) sequences them into milestones;
the **M** column below says which milestone first needs the block, and a block
is taken only as far as that milestone needs it.

## How to fill this in

The migration is top-down. Identify the top-level components of the Go system
first — the ones `docs/architecture.md` draws as boxes — then decompose each one
level at a time. Do not start at the leaves.

The test for a good entry is the garden analogy in `AGENTS.md`: a component is a
tree. It may be any shape or size, but it is clearly *itself*, and the roads
between trees are clear. The trees are the point; the roads only exist so trees
can be planted. So an entry that describes plumbing — a "manager", a "helper", a
"utils" — is not a component, and an entry whose boundary you cannot state in one
sentence is two trees that have grown together.

For reference, the upstream tree has 38 packages under `internal/`, roughly
46k lines of implementation and 24k lines of tests. Line counts, largest first:

```text
cli 9299   tool 4653   terminal 4646   process 2307   provider 2012
httpapi 1975   session 1905   agent 1757   app 1607
change 1172   protocol 1094   config 1063   store 959   subagent 874
auth 871   event 832   compaction 748   command 629   api 617
systemcontext 542   webfetch 513   client 500   skill 469
diagnostics 431   monitor 315   task 311   workspace 254
question 253   mode 248   permission 163   status 159
transport 153   atomicfile 142   appdirs 112   id 104
processidentity 103   security 76   project 50
```

A package's size is not its rank. `internal/id` is 104 lines and everything
depends on it; `internal/cli` is 9299 lines and nothing does.

## Out of scope

Upstream behaviour deliberately not carried over. Upstream tests covering it are
deleted rather than skipped, and no block in `architecture.md` absorbs it.

| Dropped | Upstream | Reason |
| --- | --- | --- |
| MCP | `internal/mcp` (2045 lines), `internal/tool/mcp.go`, MCP transport config | Dropped entirely by decision, 2026-07-24. Removes one of upstream's five extension boundaries, leaving four: provider protocols, secret storage, tools, formatters. |
| Transactional file edits | `internal/change` (1172 lines): the all-or-nothing apply, rollback, and `FileStore`/`FileState` machinery | Dropped entirely by decision, 2026-07-24. Tools write files directly. |
| Windows support | Windows paths, credential storage, process trees, terminal behaviour | Upstream targets macOS and Linux; so does this. |

## Deliberate divergences

MIGRATION.md §1 permits changing anything outside the load-bearing invariants,
*provided the divergence is recorded*. This is where. An undocumented change is
indistinguishable from a porting mistake when a test fails six components later.

### User sessions are isolation and ownership boundaries

**Workspace identity.** A user session records the exact launch working
directory as its workspace. That path is shared context, not private storage.
Canonical or physical path resolution is used only when comparing and claiming
workspace identity; it must not silently replace the launch path exposed to the
agent or used as its working directory.

**Private state.** Each user session has one private root under Parrot state.
Its database, queues, process-output blobs, plan artifacts, and other internal
artifacts live there and never in the workspace. Exactly one active runtime may
own that root and write its database. Child agent sessions share the owning user
session's root and cannot acquire or escape into another user session's root.

**Opening.** Fresh creation always creates a new user session and a root agent
named `main`. Exact resume opens only the requested existing session and fails
if it cannot do so safely. Default open creates when there is no matching
session, resumes when exactly one matching session can be selected, and rejects
an ambiguous set rather than choosing arbitrarily. Existing sessions preserve
legacy root-agent names; compatibility data is not rewritten merely by opening
it. A live owner is never joined or stolen.

**Management.** `SessionCatalog` is a metadata and admission surface only. It
may list sessions and decide which private root an operation is attempting to
open, but it never returns a live `UserSession`, database, queue store, or other
runtime-owned object. This keeps management reads from becoming a second owner.

**Protected roots.** Built-in filesystem tools enforce a mandatory protected-
root policy in addition to profile sandbox rules. Parrot's private state,
configuration, and data roots are inaccessible through `read`, `glob`, `grep`,
`write`, `edit`, and `apply_patch`; nesting one of those roots beneath a
workspace or reaching one through a symlink does not weaken the rule. A profile
cannot override it. Plan artifacts and overflow blobs are exposed only through
narrow, runtime-granted capabilities owned by their components, not by making a
private root generally readable or writable. Filesystem permission never
implies network permission.

**Serving.** The default remote-capable transport is a Unix-domain socket in a
user-only control directory: the directory is mode `0700` and the socket is
mode `0600`. TCP is explicit and authenticated. Binding plaintext TCP beyond
loopback additionally requires an explicit unsafe acknowledgement, emits a
warning, and is intended to sit behind a secure proxy. Authentication and
filesystem ownership are admission checks, not substitutes for user-session
ownership inside the service.

### Tasks do not nest

**Upstream.** A task may have a parent task, so tasks form a tree rooted at the
session's main task; `task.start` carries `parent_task_id`; the main task of a
subagent child session is the subagent task itself.

**Here.** A task belongs to exactly one session — the session that started it is
its parent. Sessions nest via `parent_session_id`; tasks do not. There is no
`parent_task_id`, no task tree, and a session has no task id of its own.

**Why.** A client had to rebuild the task tree from `parent_task_id` to make
sense of a stream, which is work every client repeated and precisely what
`BasicCli` is not allowed to do. Recursion now has one shape instead of two,
at the session level, which is already where child lifetime and recursion
limits live.

**Affects.** The `Event` message, `TaskManager`, `AgentSession`, and the
task lifecycle events. Upstream tests asserting task parentage are rewritten
against session parentage rather than deleted — the behaviour still exists, it
is attributed differently.

### Tool execution has typed lifecycle events

**Upstream.** Tool execution state is rendered from task lifecycle events.

**Here.** A provider `ToolCallChunk` event describes one fully accumulated tool
call after its argument stream completes. Actual execution emits `ToolStarted`,
followed by exactly one of
`ToolFinished`, `ToolCancelled`, or `ToolError`. Every payload carries the tool
call id and name; errors also carry their message. Calls skipped after an
interrupt emit `ToolCancelled` without `ToolStarted` because they never ran.
Both CLIs render these events directly rather than inferring execution state
from tool-call arguments or tool-result text. `EnhancedCli` additionally renders
the completed `ToolCallChunk` as provider activity; it does not interpret it as
execution.

**Why.** Separating provider streaming from execution gives clients an
unambiguous, durable lifecycle even when enhanced chat also exposes the provider
stream for diagnostics.

**Affects.** The `Event` message, `AgentSession`, `BasicCli`, and `EnhancedCli`.

### Static musl cannot do TLS — **unresolved, needs review**

**Intended.** The shipped binary is statically linked against musl:
`-r linux-musl-x64` with `StaticExecutable=true`, no interpreter, no `NEEDED`.
That part works and is verified.

**What happens.** It cannot make an HTTPS request:

```console
$ parrot chat "..."
No usable version of libssl was found
exit 134
```

.NET resolves OpenSSL with `dlopen` at first use. A `static-pie` executable has
no dynamic loader, and musl's static `dlopen` is a stub that always fails, so
there is nothing to load OpenSSL with. .NET has no supported way to link it
statically.

Two hypotheses were tested and eliminated, because "it is structural" is the
kind of claim that is worth being wrong about:

- **Not NixOS.** The glibc AOT binary carries **no** libssl in `NEEDED` — it
  `dlopen`s OpenSSL at runtime, exactly like the static one tries to — and it
  completes the live HTTPS turn. `dlopen` resolves correctly on this system.
- **Not musl.** The failure is the *static* part, not the libc.

A third thing surfaced while testing: `-r linux-musl-x64 -p:StaticExecutable=false`
produces a **mis-linked** binary — a musl interpreter resolving against glibc
`libc.so.6`. The `musl-clang` shim in `flake.nix` was written for the static
case and is wrong for the dynamic one, so option 1 below is not currently a
working configuration either.

**So the two requirements conflict.** Fully static linking and HTTPS cannot both
hold today. Everything M1 needs works on the ordinary AOT publish, which is
still one self-contained 19 MB binary, just dynamically linked against the
system libc and OpenSSL.

**Options, none taken yet.**

1. Drop `StaticExecutable`, keep the musl RID — **needs the shim fixed first**;
   as it stands this produces a musl/glibc hybrid that does not run.
2. Keep glibc-dynamic AOT, which is what M1's live turn was verified on.
3. Keep static musl and give up HTTPS, which is not an option for this product.
4. Revisit if .NET gains static OpenSSL support.

The configuration is deliberately left as-is — `StaticExecutable=true` for the
musl RID — so the decision is visible rather than quietly reversed. Note that
`dotnet publish -r linux-musl-x64` therefore currently produces a binary that
builds, links, runs, and fails on first network call.

## Entries

One per block. Fields are: what upstream it **absorbs**, the state it **owns**
(if two components claim the same state, the map is wrong), its **inbound** and
**outbound** contracts, whether it is an **extension boundary**, and its rank.

---

## Storage

### `StatePaths` — rank 1, M1

- **Absorbs** `appdirs`, `project`, `id`, `atomicfile`, `processidentity`.
- **Owns** the resolved state, config, and data directory paths; the host key
  and process identity; id generation; the atomic write/link primitives.
- **Inbound** where does X live, what is this host called, give me a fresh id,
  write this file atomically, claim this name with `link()`. Upholds nothing on
  its own but every storage invariant is built out of its primitives.
- **Outbound** the filesystem only.
- **Boundary** no. Concrete.
- **Note** `link()` returning `EEXIST` rather than overwriting is the whole
  reason claims work. A helper that falls back to `rename` silently destroys
  the guarantee, so that fallback must not exist.
- **Divergence.** Config, state, and data use the `parrotdotnet` XDG application
  directory rather than upstream's `parrot`, preventing the two projects from
  sharing files. Old `parrot` directories are neither migrated nor consulted.

### `Configuration` — rank 1, M1

- **Absorbs** `config`, `mode`.
- **Owns** the parsed and merged configuration, and — uniquely — the
  centralized atomic state shared across hosts.
- **Inbound** the merged view for this user session, resolved once at startup;
  and read/modify of global state such as flags.
- **Outbound** `StatePaths` for atomic write.
- **Boundary** no.
- **Note** not immutable at runtime, unlike the merged view a session resolves
  from it. Rename gives atomicity, not serialisation: a flag write is safe, a
  read-modify-write against a concurrent host is not. See the configuration
  exception in `architecture.md`.
- **Built (partial), M2.5 and M8.** `config.yaml` carries `model` only; the interactive
  `/model` writes it back so the choice is the default for the next launch, the
  `--model` flag stays a per-invocation override. YamlDotNet, edited through the
  representation model so a one-field write keeps other keys — comments are the
  one thing it drops, acceptable while the file is a couple of scalars. The CLI
  owns the file I/O for now; when `ProviderRegistry` lands, reading the default
  moves domain-side, and the nested `providers:` map is the point to add typed
  parsing. Auth is deliberately a separate file (`credentials.json`), never in
  here.
- **M8.** The mode registry owns the three selectable foreground execution
  policies (`build`, `plan`, and `query`), their prompts, declared rules, limits,
  and turn hooks. Mode remains per-session state rather than a YAML key. Child
  profiles are not selectable foreground modes.

### `SessionDatabase` — rank 2, M2

- **Absorbs** `store` (database, meta), `workspace`.
- **Owns** the SQLite file and schema inside one user session's private root,
  plus its `meta.json` projection. Process-output blobs and plan artifacts also
  live beneath that root, but remain owned by their producing components.
- **Inbound** open, migrate, transact. Upholds the one-machine-one-database
  invariant, `journal_mode=TRUNCATE`, and the rule that at most one active
  runtime writes a user session's database.
- **Outbound** `StatePaths`.
- **Boundary** no.
- **Note** WAL is forbidden, not discouraged: `-shm` is memory-mapped and two
  hosts mapping it get incoherent private views. A test asserts no `-shm` or
  `-wal` ever appears under the state directory.

### `EventRepository` — rank 2, M2

- **Absorbs** `event` (persistence half).
- **Owns** the durable event log and its query projections, and the transaction
  that commits both together (principle 9).
- **Inbound** append an event, read a range. Guarantees the event and its
  projection commit atomically or not at all.
- **Outbound** `SessionDatabase`.
- **Boundary** no.

### `QueueStore` — rank 3, M8

- **Absorbs** `internal/queue` and the queue portions of upstream `tool`,
  `status`, `agent`, and `session`.
- **Owns** one user session's named JSONL queues, their metadata, lock
  discipline, durable monitored-delivery ids, and canonical monitored-item
  selection.
- **Inbound** explicitly create, inspect, list, push, take, monitor, and offer
  one monitored item. Queue names are canonical lowercase ASCII words joined by
  hyphens; empty queues remain durable.
- **Outbound** the user session's private queue directory and `UserSession` for
  trusted root-idle notification admission.
- **Boundary** no. Concrete and user-session owned; the five queue tools are
  its adapters.
- **Note** queue files use bounded, strict JSON Lines and lock directories so
  independent store instances/processes share one read-modify-write discipline.
  The queue item is removed only after a root transaction durably admits its
  stable notification id, allowing retry after either durability domain fails.

### `EventBroker` — rank 3, M1

- **Absorbs** `event` (broker, stream, subscription).
- **Owns** live subscriptions, one bounded queue per subscriber.
- **Inbound** subscribe to a session's stream; publish. Publication is
  serialised, and **only events `EventRepository` has already committed** are
  published — a subscriber cannot observe an event a crash would un-happen.
- **Outbound** `EventRepository`.
- **Boundary** no.
- **Publishing never blocks and never waits for a reader.** A subscriber that
  stops reading loses its oldest events rather than stalling the session
  publishing to it. Safe because these are live events, which principle 10
  makes disposable; durability is `EventRepository`'s job from M2. A single
  shared bounded channel — the first implementation — made a dropped listener
  look like a hang instead of a leak.
- **Subscriptions unregister on disposal**, so a departed listener stops
  receiving and stops being tracked. Disposing the broker ends every
  subscription, and disposal chains `ParrotService` → `UserSession` →
  `EventBroker`.
- **Closed in M2.** `AgentSession.Emit` appends to `EventRepository` before
  publishing, so the broker only ever hands a subscriber an event that is
  already committed. The M1 shortcut is gone. Evicting an idle *user session* is likewise deferred: M1 keeps them for
  the process lifetime, which is right for a one-shot CLI and wrong for
  `parrot serve` at M6.

---

## Providers

### `ICredentialStore` — rank 3, M1

- **Absorbs** `auth`, `security`.
- **Owns** stored credentials, keyed by provider id, in the private config
  directory.
- **Inbound** get, set, delete. Nothing else — it knows nothing of OAuth,
  expiry, or refresh.
- **Outbound** `StatePaths`.
- **Boundary** **yes** — secret storage.
- **Note** a credential must never reach a log, an event, or an error message.

### `ILLMProvider` — rank 5, M1

- **Absorbs** `provider`, `protocol`.
- **Owns** nothing. Stateless by rule: no conversation, no session, no history
  between calls.
- **Inbound** `Call(LLMRequest, CancellationToken)` →
  `IAsyncEnumerable<LLMEvent>`, terminated by a `Completed` event carrying the
  finish reason and token counts. The last event is the durable outcome, so
  there is no second return channel.
- **Outbound** `ICredentialStore` only. It owns its own credential refresh,
  because only it knows its token lifecycle.
- **Boundary** **yes** — provider protocols.
- **Note** the wire shape is confirmed against a live OpenCode Go call, not
  read from documentation: SSE `data:` lines carrying `choices[0].delta`, where
  `content` and `reasoning_content` are **separate fields** — which is why
  `LLMEvent` has both `TextDelta` and `ReasoningDelta`.
- **Gotcha** on a reasoning model, `max_tokens` covers reasoning *and* content.
  A budget of 60 against `glm-5.2` produced 19 reasoning deltas, zero text
  deltas, no text at all, and `finish_reason: length`. That is a
  successful call that looks like a broken one, so a too-small budget must be
  reported as a budget problem rather than surfaced as an empty reply.

### `ProviderRegistry` — rank 5, M1

- **Absorbs** the preset and catalogue half of `provider`.
- **Owns** the configured and built-in providers, and the merged model
  catalogue.
- **Inbound** resolve `provider/model` to an `ILLMProvider` and a model; list
  models. The model portion keeps any vendor prefix (split on the first slash),
  so `openrouter/openai/gpt-4o` resolves to provider `openrouter`, model
  `openai/gpt-4o`.
- **Outbound** `Configuration`, `ICredentialStore`.
- **Boundary** no.
- **Note** the catalogue lives on the registry rather than on `ILLMProvider`,
  so a provider stays stateless: it can list models, but remembering them is the
  registry's job. The registry is seeded with preset and declared metadata so a
  model is selectable offline. User-facing listing checks credentials each time,
  skips uncredentialed providers without contacting them, and overlays what each
  available endpoint serves on a best-effort basis.

### `ILLMProvider` sub-decomposition (ported 2026-07-24)

The full provider ecosystem was ported from Go. Each type is in `Parrot.Llm`;
the two wire dialects and the shared HTTP/SSE machinery are in `Parrot.Llm.Wire`.

- **Wire adapters.** `SseDecoder`; `ChatCompletionsAdapter` and
  `ResponsesAdapter` (each `Encode` + `Parse`, static-testable), absorbing
  `protocol/{sse,chatcompletions,responses}`. `HttpStreaming` absorbs
  `provider/http.go` — endpoint/header validation, header timeout, bounded
  stream, structured `ProviderHttpException`. Both adapters speak the existing
  tool vocabulary: they encode `LLMToolDefinition` and prior
  `LLMMessage.ToolCalls`/`ToolCallId`, and their terminal `Completed` carries the
  assembled `AssistantText` and `LLMToolCall`s, exactly as the chat-completions
  provider already did.
- **Providers.** `OpenAICompatibleProvider` (protocol-selectable, now built on
  the adapters); `OpenCodeGoProvider` and `KimiProvider` **compose** it (no
  inheritance, per `AGENTS.md`) and add usage; `ChatGptProvider` (OAuth,
  responses dialect, fixed endpoints). `openrouter` and `kimi-code` are the base
  provider plus a decoder, matching upstream having no dedicated type.
- **Model catalogue.** `IModelListDecoder` + `Standard`/`OpenRouter`/`Kimi`
  decoders (ChatGPT decodes inline); `ModelCatalogue.Merge`; `LLMModel` grew
  metadata (context window, prices, `ModelCapabilities` with reasoning variants).
- **Usage.** `IUsageReporter` (optional capability) with `SubscriptionUsage`,
  implemented by ChatGPT, OpenCodeGo, Kimi. Implemented but not yet surfaced in
  the CLI (upstream shows it in status).
- **Retry + classification.** `ProviderErrors` (`IsUsageLimit`/
  `IsEngineOverloaded`) and `RetryingProvider`, a decorator the registry wraps
  around every provider, folding upstream's header-retry and stream-retry layers.
- **Presets + build.** `ProviderPresets` absorbs `app/presets.go`;
  `ProviderRegistryBuilder` absorbs `app.BuildProviders` (env-var → credential
  key resolution, preset merge, retry wrapping). `ProviderRegistry` absorbs
  `agent/provider.go`.

### `ICredentialStore` — schema change (2026-07-24)

The store now holds a versioned `Credential` **tagged union** (api-key or OAuth),
keyed by name, on disk as `{version, credentials:{name: Credential}}` with
0600/0700 permissions, atomic write, strict JSON, and validate-on-read. This is
a deliberate schema change from the previous `{providerId: "secret"}` map; no
migration is required (§1: no compatibility with prior state). OAuth adds
`OAuthTokenSource` (5-minute-early refresh, single-flight, rotated tokens
persisted), `OpenAiOAuthClient` (PKCE browser flow on a loopback listener plus a
device-code fallback), and `IBrowserOpener`, absorbing `auth`, `security`.

### Divergences recorded

- **`redactingStream` is deliberately omitted.** Secrets are not scrubbed from
  event or error text; error messages are still control-char sanitised and
  length-bounded.
- **A structured error inside a 200 stream is raised, not emitted.** Upstream
  yields an `EventProviderError`; here the adapters throw
  `ProviderResponseException`, which the retry layer classifies exactly as it
  classifies an HTTP failure. This keeps `LLMEvent` unchanged.
- **Router metadata is not surfaced.** The `provider` object OpenRouter returns
  is parsed but dropped, since no consumer exists.
- **A session re-resolves its provider on selection change.** `ParrotService`
  resolves `provider/model` at `CreateSession` and again at `UpdateSession`;
  the user session holds the resolved provider and model, and assigns both to
  the main agent session. It no longer rebuilds that session: with a drain, the
  session holds the conversation, the input admitted against it and possibly a
  turn in flight, and replacing it to change a model threw all three away.
- **`config.yaml` gains a `providers:` map** (`ProviderConfig`/`ModelConfig`)
  for custom compatible providers and per-model overrides.
- **CLI strings changed:** `auth login <provider> [--api-key-stdin]` and
  `auth login chatgpt [--device]`; `/auth login [provider]` in the REPL, where
  omitting the provider presents the buildable provider choices.
- **No same-origin redirect following** (upstream refuses cross-origin only):
  the provider `HttpClient` disables auto-redirect and any 3xx is an error.
- **API keys are resolved per request, not at startup.** Every provider holds an
  `IApiKeySource` that reads the environment variable or credential store on each
  call, so `auth login` takes effect immediately without a restart. The registry
  is built once with all configured providers regardless of whether a credential
  exists yet; user-facing model listing dynamically filters those providers by
  credential availability, while a missing key on a direct call surfaces as a
  non-retryable `LLMProviderException`.

---

## Domain

### `TaskManager` — rank 4, M5

- **Absorbs** `task`, `status`, `monitor`.
- **Owns** the tasks belonging to one session and their lifecycle state.
- **Inbound** start, observe, complete a task. Tasks are flat: a task's parent
  is the session that started it, and tasks do not nest.
- **Outbound** `EventBroker`.
- **Boundary** no.
- **M8.** The status slice observes selection and active user-session work
  through typed providers with stable namespaced keys. It deterministically
  composes nonblank observations for `AgentSession`; active shell processes and
  child agents are observed through their owning objects rather than inferred
  from IDs. Generic task commands and protocol-level task correlation remain
  deferred.

### `PermissionBroker` — rank 5, M3

- **Absorbs** `permission`.
- **Owns** pending write-permission requests and the runtime grants accepted
  for the requesting `AgentSession`.
- **Inbound** `request_write_permission` requires one or more exact existing
  absolute paths and a nonblank reason. The broker resolves canonical physical
  targets: a file grant is exact-file; a directory grant includes descendants.
  It authorises a **canonical operation**, never a tool name (principle 7).
  Only the server-declared Grant, Reject, and Reject-with-reason replies are
  accepted; cancelled selection or reason entry is Reject, and a blank required
  rejection reason is invalid. Noninteractive sessions reject immediately and
  pending requests time out. Authorisation stays separate from OS containment
  (principle 8).
- **Outbound** typed permission requests and replies through `EventBroker`,
  `Configuration` for standing grants, and the requesting agent's sandbox for
  accepted runtime grants.
- **Boundary** no. A grant enables write, edit, and shell access within its
  target, is runtime-only and nonpersistent, and does not transfer to child or
  sibling agents or merge into `SecurityProfile`. A read-only profile, explicit
  static deny, and protected roots override it; it has no network effect.

### `QuestionBroker` — rank 5, M3

- **Absorbs** `question`.
- **Owns** pending questions and their answers.
- **Inbound** ask the user a structured question, await the answer.
- **Outbound** `EventBroker`.
- **Boundary** no.

### `SystemContextBuilder` — rank 7, M4

- **Absorbs** `systemcontext`, `skill`, `command`.
- **Owns** the typed context sources: base prompt, date, platform, working
  directory, project metadata, `AGENTS.md` files, skills, tool guidance.
- **Inbound** sample the sources and produce an epoch baseline. **Sampled only
  at a safe turn boundary** (principle 4).
- **Outbound** `Configuration`, `StatePaths`, the filesystem.
- **Boundary** no.

### `Compactor` — rank 8, M4

- **Absorbs** `compaction`.
- **Owns** the compaction record and the history cutoff it produces.
- **Inbound** compact this history; completing starts a new epoch.
- **Outbound** `ILLMProvider` to summarise, `SessionDatabase` to persist.
- **Boundary** no.

### `AgentSession` — rank 9, M1

- **Absorbs** `session` (conversation half), `agent` (runner and coordinator).
- **Owns** identity, selection, drain state, interactive owner binding,
  admitted input, messages, context epoch, todos, goals, and its tasks. Todos
  and goals are **owned sub-objects**, not services.
- **Inbound** admit a prompt, run the drain, interrupt. Upholds principles 2
  (one drain), 3 (a turn is a cancellable boundary), 4 (immutable epoch), and 6
  (all tools settle before the next turn).
- **Outbound** everything in the turn sequence: `SessionDatabase`,
  `EventBroker`, `SystemContextBuilder`, `Compactor`, `AgentRegistry`,
  `IToolFactory`, `ProviderRegistry`, `TaskManager`.
- **Boundary** no. Concrete, and rich — never a record plus a service.
- **Note** it is also the `ILLMEventSink` implementer, attaching `session_id`
  and `task_id` to make a wire `Event` from an `LLMEvent`.
- **M8.** It owns the selected foreground mode and the pending/consumed runtime
  status transition. Status is appended atomically as typed, sequenced `system`
  history before the first real provider call and after an actual mode change;
  it is not part of the immutable epoch baseline. Provider/model-only updates,
  idle drains, interruptions, and tool rounds do not duplicate it.

### `AgentSession` — todos (ported 2026-07-24)

`TodoCollection` is the session-owned todo sub-object. `todoread` returns its
ordered durable state, while `todowrite` validates and transactionally replaces
the complete list, assigning ids and positions where required. A successful
replacement records a `TodoUpdated` event in the same database transaction and
then publishes it, so persisted state and replay cannot disagree. Todo rows and
events are scoped by agent session; the tools reach the sub-object through the
`AgentSession` they are constructed for rather than through a separate service.

### `AgentSession` — admitted input and the drain (ported 2026-07-24)

`Admit` records a prompt durably and wakes the drain; `Interrupt` stops the turn
and waits for it to have stopped. One drain owns a session (principle 2), and a
prompt arriving during a turn coalesces into it rather than starting a second.
The turn sequence is the one `docs/architecture.md` fixes, and its two promotion
points are the whole difference between the deliveries: every pending steer is
promoted at each turn boundary, and one queued prompt is promoted only where the
turn would otherwise stop.

Divergences from upstream `session.Service` / `agent.agentSession`:

- **No steer cutoff.** Upstream promotes steers admitted at or before a sequence
  cutoff, comparing an input's `admitted_sequence` against the latest message
  sequence in one shared event-sequence space. Here `event`, `message` and
  `input` have separate autoincrements, so the cutoff has nothing to mean. The
  single transaction is the boundary instead: a steer admitted while the
  promotion runs lands at the next one.
- **An interrupted turn ends as `TurnEnded { finish_reason = "interrupted" }`,**
  not as a payload of its own, and records `(interrupted)` where the answer
  would have been. Without that message the history ends on the prompt that was
  stopped, and the next drain reads it as still owed an answer — so
  interrupting a turn would start it again.
- **`queue` is reachable by any client, and neither CLI sends it,** exactly as
  upstream's two CLIs send only `steer`.
- **Pending input is not replayed at startup.** The `input` table makes a queued
  prompt survive the process, but nothing promotes it on the next run yet.
  Recovery belongs to `UserSession`'s reclaim path.
- **The drain task is a field, not an awaited descendant of a `Run`.** MIGRATION
  §3 wants no abandoned task; this replaces the fire-and-forget `Run` per prompt
  with one joinable drain per session. `Interrupt` awaits it, and
  `UserSession`/`ParrotService` are `IAsyncDisposable` so that ending a session
  means waiting for its drains and only then closing what they write to —
  `await using var composition` in `CommandDispatcher`, with Pure.DI disposing
  the service before the store it depends on. A separate `Settle()` the caller
  had to remember was the same crash on any path that forgot it or threw.
- **`EventRepository` serialises every call.** One `SqliteConnection` holds one
  transaction at a time, and admitting now happens on the request's thread while
  the drain writes on its own, so a second writer is a corrupted connection
  rather than a slow one.

### `AgentRegistry` — rank 9, M5

- **Absorbs** `agent` (registry, provider resolution), `subagent`.
- **Owns** agent profiles, the user-session-wide canonical child session table,
  per-parent direct-child friendly-name namespaces, recursion limits, and
  **the lifetime of every spawned child session**.
- **Inbound** resolve an agent profile; create and retrieve a child session.
  Friendly names are unique and resolvable only among one caller's direct
  children; canonical session ids remain resolvable throughout the user session.
  Send addressing resolves an exact canonical spawned-agent id first. For a
  sender with a registered direct parent, the case-sensitive literal `parent`,
  actual parent id, or actual parent friendly name resolves second and takes
  precedence over a colliding direct-child friendly name; direct-child names
  resolve last. A root therefore falls through and may resolve its direct child
  named `parent`. `wait_agent` remains child-only and retains ordinary child
  resolution without a parent alias.
- **Outbound** `Configuration`, `AgentSession`.
- **Boundary** no.
- **Note** mutually dependent with `AgentSession`; both rank 9. The registry
  creates, names, retains, observes, and owns the lifetime of background child
  sessions. It does not admit input or wait for turns: those operations belong
  to the retrieved `AgentSession`, keeping one owner for drain concurrency and
  terminal results. `agent_spawn` returns immediately and `wait_agent` waits or
  yields without canceling the child. Each child publishes a durable
  `AgentStarted` event followed by exactly one `AgentFinished` or `AgentFailed`
  event; cancellation is a failure carrying the retained interruption message.
  User-session shutdown cancels and joins every child. Profiles, generic task
  APIs, and the remaining `TaskManager` work stay deferred rather than stubbed.
  M8 adds only foreground profiles and typed observation of the existing child
  lifecycle.
- **Completion delivery.** Absorbing the terminal notification path from
  `internal/agent`, `internal/tool`, and the former `internal/subagent`
  completion notifier, the registry resolves only a child's registered direct
  parent and admits a bounded, trusted terminal notification there. A child
  execution reports after its durable terminal lifecycle event; an idle child
  parent receives it through its own execution lifecycle so nested completions
  propagate one level at a time. The root admits it as ordinary steering input.
  `wait_agent` remains a retained-result read and never creates a notification.
  Unlike upstream's process-wide asynchronous notifier, this registry-scoped
  path is awaited by the terminal child execution and is bounded by the
  user-session registry lifetime. Parent delivery failures are best effort and
  cannot alter the child's retained terminal result.

### `UserSession` — rank 10, M2

- **Absorbs** `session` (`InteractiveOwner`, `InteractiveClaim`), `store`
  (owners, claims).
- **Owns** its private root, the exclusive runtime claim on it, the exact launch
  working-directory binding, its shared durable queue store, and the
  `AgentSession`s inside it. Root-idle monitored queue delivery is admitted here
  rather than by child sessions.
- **Inbound** create a fresh session, resume an exact id, or use default-open
  cardinality semantics for a workspace. The exact launch path is retained for
  execution while canonical identity is used only for matching and claims.
- **Outbound** `SessionDatabase`, `StatePaths`, `Configuration`, and its private
  queue directory.
- **Boundary** no.
- **Note** one claim is held for exactly the duration of `Run`. A live owner is
  never joined or displaced. Fresh roots use `main`; resumed legacy roots retain
  their stored root-agent name.

### `SessionCatalog` — rank 10, M6

- **Owns** the management projection of user-session metadata and admission
  decisions; it owns no live runtime state.
- **Inbound** list metadata, create fresh, resume an exact id, or default-open.
- **Outbound** `StatePaths` and the component that starts a `UserSession` only
  after admission succeeds.
- **Boundary** no. It must never hand management callers a live session,
  database, queue, or repository.

---

### M8 wire and presentation divergences

- `mode` is a first-class field on the .NET session create/update/response
  contract rather than upstream's compatibility alias for `agent`; this port
  does not expose foreground profiles as agents.
- A status injection publishes a small transient protobuf event after the
  durable system message commits. The prompt text stays in message history and
  is not duplicated on the live wire.
- `/mode` and `/modes` select and discover foreground policies. `/status` remains
  deferred because it is a separate user-facing summary, not status-prompt
  injection. Basic and Enhanced render the transient notification independently.
- Profile tool capabilities remain distinct from the mandatory protected-root
  boundary. `query` and `plan` apply their configured workspace policy, while
  the plan artifact receives only its runtime-owned narrow capability. Plan
  approval dialogs remain separate from filesystem isolation.

---

## Tools

### `ITool` — rank 7, M3

- **Absorbs** `tool` (the interface and the builtins).
- **Owns** nothing shared; each tool owns its own arguments and plan.
- **Inbound** describe, plan, execute. **Display differences are methods on the
  tool, never a branch on its id.** `exec_command` accepts optional `name`,
  `yield_after_ms`, and `env`, where `env` is a string-to-string map represented
  internally by concrete `ProcessEnvironmentOverrides`, constructed from
  `IEnumerable<KeyValuePair<string, string>>` (`Empty` when omitted). A
  non-object `env` reports
  `error: Tool argument 'env' must be an object containing string values.`; a
  non-string property reports
  `error: Tool argument 'env' must contain only string values.`; and a name
  that is empty or contains `=` or NUL, or a value containing NUL, reports
  `error: Tool argument 'env' contains an invalid environment value.`
  `wait_process` requires `name` and accepts
  optional `yield_after_ms`; `interrupt_process` requires `name` and cancels the
  named process tree. A yield returns the reserved process name without stopping
  it, and a later completion is delivered to the invoking agent through its
  durable steer queue unless a successful wait or interrupt claims it. `wait_process`
  replaces the earlier `wait_shell` name so the lifecycle tools use process
  terminology.
- **Builtin mutations.** `write` creates or replaces one file with exact UTF-8
  content. `edit` performs exact ordinal string replacement; without
  `replace_all` it requires exactly one match, while `replace_all` permits zero
  or more. Both write directly under the active filesystem security profile;
  they do not restore the dropped transactional change machinery.
- **Protected roots.** `read`, `glob`, `grep`, `write`, and `edit`
  additionally enforce Parrot's mandatory protected-root policy.
  Profile rules cannot grant access to state, configuration, or data roots,
  including when nested beneath the workspace or reached through a symlink.
  Runtime-owned plans and blobs use narrow capabilities rather than an
  exception for their containing root.
- **Outbound** `PermissionBroker`, the invoking agent session's shell-process
  owner, `ProcessRunner`, `WebFetcher`, the filesystem.
- **Boundary** **yes** — tools.
- **Divergence** `grep` uses .NET's `RegexOptions.NonBacktracking` engine
  rather than Go's RE2. The two reject the same pathological inputs (both
  guarantee linear time), but the accepted syntax differs: .NET non-backtracking
  does not support backreferences, lookaheads, or lookbehinds, while RE2 does
  not support backreferences either but has a different unicode class syntax.
  The tool description says ".NET non-backtracking regular expressions" rather
  than claiming RE2 compatibility.

### `IToolFactory` — rank 7, M3

- **Absorbs** the registry half of `tool`.
- **Owns** how one tool is built: a factory per tool, living for one
  `UserSession` and so able to take it by constructor, yielding one `ITool`
  instance per `AgentSession`.
- **Inbound** create.
- **Outbound** `Configuration`, and the sessions a tool is constructed with —
  the one place rank 7 names rank 9 and 10, granted deliberately in
  `architecture.md`.
- **Boundary** no — `ITool` is the boundary, and a new tool brings a factory
  with it.
- **Note** a session's tool set is fixed once built, so there is no mutable side
  and no registry. `ToolSnapshot` still materialises it once per turn, which is
  what principle 4 asks for.

### `ProcessRunner` — rank 6, M3

- **Absorbs** `process`.
- **Owns** OS child execution, output capture, output storage, and the sandbox.
  A per-`AgentSession` shell-process owner owns named run state and delivery. A
  user-session coordinator creates and observes those owners and joins all of
  them at shutdown without exposing process lookup or control.
- **Inbound** `Run(string command, ProcessEnvironmentOverrides environment,
  UserSessionResources resources, SecurityProfile securityProfile,
  SandboxWriteGrantSnapshot writeGrants, CancellationToken cancellationToken)`.
  An agent-session shell-process owner starts named runs through
  `Start(string? requestedName, string command,
  ProcessEnvironmentOverrides environment, AgentSession agent,
  SecurityProfile securityProfile, SandboxWriteGrantSnapshot writeGrants)`.
  By default the child inherits the complete
  launch environment. Explicit overrides replace inherited child variables and
  become deterministically sorted bubblewrap `--setenv` entries. No runtime
  environment variables are protected, cleared, or forced. **Fails closed**: no
  sandbox, no execution. Not a warning, not a fallback. The sandbox provides
  filesystem and process isolation; environment selection remains command
  execution configuration. The working directory and its Git repository root
  are writable; the latter is detected from linked-worktree metadata when the
  worktree lives outside the repository. The user's `~/.cache` directory is also
  writable so sandboxed developer tools can persist their caches. The rest of
  the host, including `~/.config`, remains read-only.
  Stdout and stderr retain at most 65,536 characters
  each in memory; if either exceeds that bound, the complete result is persisted
  in the owning user session's private blob directory and the tool returns its
  full absolute path. A narrow runtime capability permits access to that artifact
  without opening the private root. Child agents share their owning user
  session's private root.
  Process names are ordinal and unique among running processes within their
  owning agent session: supplied duplicates fail before launch while the current
  binding is running, completed bindings can be replaced atomically, and omitted
  names are generated and reserved atomically. A completed binding remains the
  current lookup target until it is replaced, subject to the existing wait and
  delivery claim rules. Wait and interrupt can address only that agent's current
  binding, while the owner retains every launched process for settlement.
  User-session-wide active-work observations qualify repeated local names with
  the owning agent session id. Runs use the user-session lifetime token, survive
  tool-call yield and cancellation, and all per-agent owners are cancelled and
  joined when that user session is disposed.
- **Outbound** bubblewrap on Linux, Seatbelt on macOS, and
  `sessions/<id>/blob` for overflow output.
- **Boundary** no.
- **Dependency.** Atrox Haikunator.NET (`Haikunator` 3.0.1) generates safe blob
  basenames; a successful Native AOT publish is its compatibility proof.
- **Scope.** This is bounded process-output capture plus session-owned named
  shell processes, yielding, waiting, and asynchronous completion delivery. It
  adds no general blob protocol, quotas, `read_output`, or PTY.

### `WebFetcher` — rank 6, M3

- **Absorbs** `webfetch`.
- **Owns** nothing across calls.
- **Inbound** fetch a URL, bounded in size and time.
- **Outbound** the network.
- **Boundary** no.

---

## Transport

### `ParrotService` — rank 11, M1

- **Absorbs** `api/v1`, `httpapi` (backend half), re-specified as a `.proto`.
- **Owns** nothing. It translates the contract into domain calls.
- **No `task_id` yet.** Nothing starts a task until M5 — no shell, no
  subagents — so `Event` does not carry one. A field holding a fabricated id
  that no client can use and no test can exercise is the "stub that returns a
  plausible value" MIGRATION.md §7 forbids. It arrives with `TaskManager`.
- **Inbound** calls are in terms of **user sessions**. Management includes
  server-authoritative session listing and distinct fresh-create, exact-resume,
  and default-open admission semantics; interaction includes session update,
  message admission, interruption, questions, and listening. Management sees
  catalog metadata, never live runtime objects. Upholds principle 11 — local and
  remote use one contract.
- **Agent sessions are not addressable.** A user never spawns a subagent; an
  agent does, into a background child session. So there is no parent id on the
  wire and no way to ask for one. An `Event` names the agent session that
  produced it, which may be a subagent, and they all surface on the one user
  session stream — which is why a client needs a single subscription however
  deep the recursion goes.
- **`Listen` is indefinite.** It ends when the client stops listening, not when
  a turn finishes, because a subagent keeps publishing long afterwards. The
  client decides when it has heard enough; `BasicCli` cancels on `TurnEnded`.
- **The model is session state, not a message property.** Selection belongs to
  `AgentSession`, so changing it is an explicit `UpdateSession` rather than a
  different value on the next prompt. A session is created explicitly too:
  get-or-create on first message cannot express a parent session, and hides the
  difference between resuming a session and starting one.
- **Why they are separate** a prompt is durable before execution is requested
  (principle 1), so admitting one must not depend on anyone listening; a client
  that drops must be able to resume the stream without re-sending the prompt;
  and more than one client can watch a session. A single `Chat(prompt) ->
  stream` conflates the command with the subscription and can express none of
  that.
- **Outbound** `UserSession`, `AgentSession`, `TaskManager`,
  `PermissionBroker`, `QuestionBroker`, `EventBroker`.
- **Boundary** no.
- **Note** request handlers must not construct long-lived dependencies.

### `InProcessChannel` — rank 11, M1

- **Absorbs** `transport`, `client`.
- **Owns** nothing.
- **Inbound** a gRPC `CallInvoker` that reaches `ParrotService` directly.
  Local mode **binds no socket** (principle 12).
- **Outbound** `ParrotService`.
- **Boundary** no.

### `GrpcServer` — rank 12, M6

- **Absorbs** `httpapi` (server, routes).
- **Owns** the listener, transport admission, and their lifetime.
- **Inbound** `Run` until cancelled.
- **Outbound** `ParrotService`.
- **Boundary** no.
- **Default transport.** A Unix-domain socket in a user-only control directory;
  the directory is mode `0700` and the socket is mode `0600`. Local in-process
  operation remains socket-free.
- **TCP transport.** TCP must be requested explicitly and authenticated.
  Plaintext non-loopback binding additionally requires an explicit unsafe
  acknowledgement, emits a warning, and is suitable only behind a secure proxy.
  No unauthenticated TCP fallback is permitted.
- **Note** optional and separately lifecycled. Its ~5 MB is the accepted cost
  recorded in `architecture.md`. Transport admission does not replace domain
  ownership checks.

---

## Root and clients

### `ParrotApplication` — rank 12, M6

- **Absorbs** `app`.
- **Owns** every singleton, constructed explicitly by hand. **No IoC
  container** — also an AOT requirement, since registration by scanning is the
  reflection MIGRATION.md §2 forbids.
- **Inbound** build the object graph; dispose it.
- **Outbound** everything.
- **Boundary** no.

### `Program` — rank 13, M1

- **Absorbs** `cmd/parrot/main.go`.
- **Owns** the process: the cancellation token source and the signal
  registrations that trip it.
- **Inbound** `Main(string[])`. Hands the argument vector to
  `CommandDispatcher.Run` and returns its exit code.
- **Outbound** `CommandDispatcher`.
- **Boundary** no.
- **Note** uses `PosixSignalRegistration` for `SIGINT` and `SIGTERM`, not
  `Console.CancelKeyPress`, which is a .NET event. It is the only place allowed
  to block on async, and it does not.

### `CommandDispatcher` — rank 13, M1

- **Absorbs** `cli/cli.go`, `diagnostics`.
- **Owns** the argument vector and the process exit code.
- **Inbound** **the single top-level `Run`.** Every other `Run` is a
  descendant; the process exits when it returns.
- **Outbound** `ParrotApplication`, `BasicCli`, `EnhancedCli`, `GrpcServer`.
- **Boundary** no.

### `BasicCli` — rank 13, M1

- **Absorbs** `cli/chat`, rewritten far smaller. **Not** `cli/chatview`.
- **Owns** nothing. No model of the conversation beyond what it has printed.
- **Inbound** a `switch` over `Event.PayloadCase` and a `WriteLine`, driven
  either one-shot or by `InteractiveSession`'s REPL. Slash commands are
  client-side and never reach the event stream, so they do not give it a
  conversation model.
- **Outbound** the generated gRPC client, and nothing else.
- **Boundary** no.
- **Note** it is the test of the event contract. If it needs a helper, fix the
  event. Shares no code with `EnhancedCli` beyond the generated stub.

### `EnhancedCli` — rank 14, M7

- **Absorbs** `cli/enhancedchat`, the streamed-turn part of `cli/chatview`, and
  the live-row part of `terminal`.
- **Owns** two sub-components. The **turn view** interprets typed gRPC events and
  accumulates the foreground assistant text for one turn. The **live terminal
  renderer** owns display-width layout, sanitisation, bounded mutable rows,
  serialized ANSI cursor operations, the raw-mode thinking animation, and
  promotion of stable rows into ordinary terminal scrollback.
- **Inbound** the same event stream and payloads as `BasicCli`; every event is
  rendered so admission, promotion, turn, retry, provider, tool, agent, and
  completion activity remains observable. In raw mode event activity replaces
  the colored live buffer rather than entering scrollback; only explicit
  transcript commits move upward. Terminal width is sampled while laying out
  each enhanced frame.
- **Outbound** the generated gRPC client and a `TextWriter` representing the
  terminal. Provider text crosses this boundary only after control-character
  sanitisation.
- **Boundary** no. Both sub-components remain private to the
  `Parrot.Cli.Enhanced` package; neither is shared with `BasicCli` or moved into
  the domain.
- **Note** complete physical assistant rows become immutable scrollback while
  only the unfinished final row remains redrawable. No alternate screen is
  used. On a supported terminal the raw-mode editor provides rune-aware cursor
  movement, multiline input, bracketed paste, editing controls, and a modeline;
  line-buffered input remains the fallback when raw mode is unavailable or
  `TERM=dumb`. After prompt submission, a raw-mode thinking frame animates until
  the first streamed event. Each later activity event replaces that live frame,
  whose distinct background separates it from permanent scrollback, while one
  modeline and editor remain below it. Completion, interruption, EOF, cancellation,
  and failures cancel and join the animation before clearing it. Assistant text
  uses the upstream terminal-safe Markdown subset: headings, blockquotes, lists,
  task boxes, thematic rules, tables, inline emphasis/code/links, and fenced code.
  Known fence languages use an embedded, AOT-safe lexical colorizer with the
  upstream ANSI token palette; unknown/plain and no-color output remain plain.
  Highlighting is bounded at 512 KiB and 10,000 lines. Picker, modal prompts, and
  richer multi-row activity frames remain deferred M7 work.

- **Divergence.** Chroma has no .NET port, and adding a reflection-discovered
  grammar package would violate Native AOT. The embedded colorizer recognizes a
  finite language list and preserves multiline block comments and Python triple
  strings, but its token classification is deliberately more conservative than
  Chroma's full grammars. Markdown structure, fallback rules, colors, and safety
  limits remain the same.

## Assemblies

Namespaces are the unit of separation in this repository, not assemblies. The
current set is `Parrot.Core` and `Parrot.Cli`. Adding a third project requires a
reason recorded here.

| Assembly | Reason |
| --- | --- |
| `Parrot.Core` | Everything that is not the process entry point. |
| `Parrot.Cli` | The AOT-published executable. Separated so that `Parrot.Core` can be referenced by a test host without dragging in the entry point. |
| `Parrot.Analyzers` | Build tooling, not product code. Repository-specific lint rules that no shipped analyzer expresses (`PARROT0001`–`PARROT0003`). Must be a separate `netstandard2.0` project because it runs inside the compiler; referenced as an analyzer, so it never reaches the runtime or the AOT link. |
