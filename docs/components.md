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
| Transactional file edits | `internal/change` (1172 lines): the all-or-nothing apply, rollback, and `FileStore`/`FileState` machinery | Dropped entirely by decision, 2026-07-24. Tools write files directly. `apply_patch` is kept and the patch model and parsing survive with it, folded into the tool. It applies directly, so a failed apply can leave files partially written and must report what it wrote. |
| Windows support | Windows paths, credential storage, process trees, terminal behaviour | Upstream targets macOS and Linux; so does this. |

## Deliberate divergences

MIGRATION.md §1 permits changing anything outside the load-bearing invariants,
*provided the divergence is recorded*. This is where. An undocumented change is
indistinguishable from a porting mistake when a test fails six components later.

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
- **Built (partial), M2.5.** `config.yaml` carries `model` only; the interactive
  `/model` writes it back so the choice is the default for the next launch, the
  `--model` flag stays a per-invocation override. YamlDotNet, edited through the
  representation model so a one-field write keeps other keys — comments are the
  one thing it drops, acceptable while the file is a couple of scalars. The CLI
  owns the file I/O for now; when `ProviderRegistry` lands, reading the default
  moves domain-side, and the nested `providers:` map is the point to add typed
  parsing. Auth is deliberately a separate file (`credentials.json`), never in
  here.

### `SessionDatabase` — rank 2, M2

- **Absorbs** `store` (database, meta), `workspace`.
- **Owns** one SQLite file per user session, its schema, and the `meta.json`
  projection published beside it.
- **Inbound** open, migrate, transact. Upholds the one-machine-one-database
  invariant and `journal_mode=TRUNCATE`.
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
- **Owns** stored credentials, keyed by provider id, in the private data
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
  model is selectable offline, and `RefreshAll` overlays what each endpoint
  serves — best effort, since a provider without a credential simply keeps its
  seed.

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
  the user session holds the resolved provider and model, and a `/model` that
  crosses providers rebuilds the main agent session with the new provider.
- **`config.yaml` gains a `providers:` map** (`ProviderConfig`/`ModelConfig`)
  for custom compatible providers and per-model overrides.
- **CLI strings changed:** `auth login <provider> [--api-key-stdin]` and
  `auth login chatgpt [--device]`; `/auth login <provider>` in the REPL.
- **No same-origin redirect following** (upstream refuses cross-origin only):
  the provider `HttpClient` disables auto-redirect and any 3xx is an error.
- **API keys are resolved per request, not at startup.** Every provider holds an
  `IApiKeySource` that reads the environment variable or credential store on each
  call, so `auth login` takes effect immediately without a restart. The registry
  is built once with all configured providers regardless of whether a credential
  exists yet; a missing key surfaces as a non-retryable `LLMProviderException` at
  call time.

---

## Domain

### `TaskManager` — rank 4, M5

- **Absorbs** `task`, `status`, `monitor`.
- **Owns** the tasks belonging to one session and their lifecycle state.
- **Inbound** start, observe, complete a task. Tasks are flat: a task's parent
  is the session that started it, and tasks do not nest.
- **Outbound** `EventBroker`.
- **Boundary** no.

### `PermissionBroker` — rank 5, M3

- **Absorbs** `permission`.
- **Owns** pending permission requests and granted scopes.
- **Inbound** authorise a **canonical operation**, never a tool name
  (principle 7). Authorisation stays separate from OS containment
  (principle 8).
- **Outbound** `EventBroker` to ask, `Configuration` for standing grants.
- **Boundary** no.

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

### `AgentRegistry` — rank 9, M5

- **Absorbs** `agent` (registry, provider resolution), `subagent`.
- **Owns** agent profiles, the child task table, per-parent concurrency limits,
  recursion limits, and **the lifetime of every spawned child session**.
- **Inbound** resolve an agent profile; spawn, await, observe, interrupt a
  child session.
- **Outbound** `Configuration`, `AgentSession`.
- **Boundary** no.
- **Note** mutually dependent with `AgentSession`; both rank 9, neither
  buildable without a stub of the other. Not passive — it has a `Run`, and a
  spawned child outlives the turn that spawned it.

### `UserSession` — rank 10, M2

- **Absorbs** `session` (`InteractiveOwner`, `InteractiveClaim`), `store`
  (owners, claims).
- **Owns** the working-directory binding, the claim on it, and the
  `AgentSession`s inside it.
- **Inbound** open a session for this working directory: reclaim an abandoned
  binding, or start a second when one is live.
- **Outbound** `SessionDatabase`, `StatePaths`, `Configuration`.
- **Boundary** no.
- **Note** the claim is held for exactly the duration of `Run`.

---

## Tools

### `ITool` — rank 7, M3

- **Absorbs** `tool` (the interface and the builtins), `change` (patch model
  and parsing only).
- **Owns** nothing shared; each tool owns its own arguments and plan.
- **Inbound** describe, plan, execute. **Display differences are methods on the
  tool, never a branch on its id.**
- **Outbound** `PermissionBroker`, `ProcessRunner`, `WebFetcher`, the
  filesystem.
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
- **Owns** child processes, their pty, their output store, and the sandbox.
- **Inbound** run a command. **Fails closed**: no sandbox, no execution. Not a
  warning, not a fallback.
- **Outbound** bubblewrap on Linux, Seatbelt on macOS.
- **Boundary** no.

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
- **Inbound** five calls, all in terms of **user sessions**. `ListModels`;
  `CreateSession(model)` and `UpdateSession(user_session_id, model)`;
  `SendMessage(user_session_id, text)`; and `Listen(user_session_id)`. Upholds
  principle 11 — local and remote use one contract.
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
- **Owns** the listener and its lifetime.
- **Inbound** `Run` until cancelled.
- **Outbound** `ParrotService`.
- **Boundary** no.
- **Note** optional and separately lifecycled. Its ~5 MB is the accepted cost
  recorded in `architecture.md`.

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

- **Absorbs** `cli/enhancedchat`, `cli/chatview`, `terminal`.
- **Owns** the terminal: raw mode, editor, picker, markdown rendering,
  scrollback.
- **Inbound** the same event stream and the same payloads, rendered richly.
- **Outbound** the generated gRPC client, the terminal.
- **Boundary** no.
- **Note** **not decomposed yet.** Deferred to M7 planning by decision 5; it is
  the only entry here that is deliberately incomplete, and nothing before M7
  depends on it.

## Assemblies

Namespaces are the unit of separation in this repository, not assemblies. The
current set is `Parrot.Core` and `Parrot.Cli`. Adding a third project requires a
reason recorded here.

| Assembly | Reason |
| --- | --- |
| `Parrot.Core` | Everything that is not the process entry point. |
| `Parrot.Cli` | The AOT-published executable. Separated so that `Parrot.Core` can be referenced by a test host without dragging in the entry point. |
| `Parrot.Analyzers` | Build tooling, not product code. Repository-specific lint rules that no shipped analyzer expresses (`PARROT0001`–`PARROT0003`). Must be a separate `netstandard2.0` project because it runs inside the compiler; referenced as an analyzer, so it never reaches the runtime or the AOT link. |
