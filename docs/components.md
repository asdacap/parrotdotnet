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
Its database, root-agent queues, process-output blobs, plan artifacts, and other
internal artifacts live there and never in the workspace. Exactly one active
runtime may own that root and write its database. Child agent sessions share the
owning user session's isolation boundary but own only lifetime-scoped queues;
they cannot acquire or escape into another user session's root.

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

**Filesystem baseline.** The host root is mounted read-only. Profile sandbox
rules can grant writes at configured paths and configured `deny_read` rules can
further narrow reads; filesystem permission never implies network permission.
Private artifacts remain owned by their user session and are exposed only through
the narrow, runtime-granted capabilities owned by their components, not by
transferring ownership to another session.

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

### Approved AgentTask graphs are explicit, private, and non-transactional

**Plan/approval pair.** Plan mode writes one correlated pair at runtime-designated
private paths: a readable Markdown plan and strict AgentTask v1 JSON. Completion
requires both files to be nonblank and the JSON to validate. The `PlanCompleted`
dialog presents the Markdown followed by the validated approved pending task
hierarchy for approval. This is the approved declaration before execution, not a
later `AgentTaskProgressSnapshot` execution tree: research patches and retry
payloads can replace a run's effective subtree without changing that approved
hierarchy. On approval, it enters build mode with both paths; the build prompt
directs `run_agent_tasks` to the approved JSON path. The tool reopens a readable
regular non-symbolic-link file and validates it immediately, so approval is not
authority for a subsequently altered artifact. This approved workflow remains
path-based even though direct callers may embed an artifact.

**Schema and scope.** The JSON envelope is
`{"schema_version":1,"tasks":[...]}`. A sibling list is nonempty. Each task
has nonblank `name`, `description`, `payload`, and `acceptance_criteria`, with
optional `dependencies` and `model`; payload is either a nonblank instruction or
a recursive nonempty sibling list. Unknown fields, null required values,
duplicate names or dependencies, missing/self/cyclic references, and
cross-level dependencies are invalid. Dependency names are case-sensitive and
local to their immediate sibling list. This makes a nested payload a hierarchy
of independently validated sibling DAGs, not one graph with globally addressable
names.

**Invocation source.** `run_agent_tasks` requires exactly one of `path` or
`artifact`. The path form performs the regular-file, symbolic-link, and invoking
security-profile checks described above. The artifact form embeds the same v1
object in the tool arguments, parses it in memory, and performs no filesystem
read or read-permission check. Both forms use the same strict parser; supplying
both or neither is invalid. Plan-approved execution continues to use `path`.

**Execution context.** `run_agent_tasks` synchronously creates fresh
retained-only children. Composite tasks use one retained composite agent for distinct research and
validation turns; that agent recursively executes and owns nested child agents. A
fresh instruction leaf creates one `agent-task-payload` child; that child
implements and verifies the instruction and remains retained for the whole leaf
invocation. It has no inherited conversation. Each retry sends a new
user prompt to that same session while retaining the prior exchange, so retained
non-system messages grow 1, 3, 5, ... across attempts. Its combined response is
parsed directly rather than producing a separate execution transcript.
Retained-only delivery prevents internal completions from steering the tool-owning
parent. Children retain the same workspace and user-session-scoped runtime
resources. These predefined profiles are spawn-visible like other child profiles.
A task-specific model is resolved normally; otherwise the selected child uses the
invoking turn's requested model.

**Research and inheritance.** Composite work begins with a mandatory research
hook that returns strict JSON containing nonblank context and, optionally, a
sparse patch to `description`, `payload`, `acceptance_criteria`, or `model`. The
patch changes only the effective in-memory task for that run, preserves omitted
fields, is revalidated, and never persists into the approved artifact. Nested
work receives labelled ancestor declarations in root-to-parent order and
research contexts in root-to-current order. It receives no sibling or cousin
research. A ready task additionally receives bounded direct-dependency summaries.

**Acceptance, scheduling, and outcome.** The retained composite agent reviews its nested result in a distinct validation
turn. Nested task agents are children owned by that composite agent. Every
instruction-leaf response must return exactly one strict verdict: `accept` with
nonblank evidence, `reject_and_halt` with nonblank feedback, or
`reject_and_retry` with nonblank feedback and a replacement payload. The
validation turn retains the existing verdict JSON forms;
a leaf's combined response additionally requires nonblank `context`:
`{"context":"nonblank","verdict":"accept","evidence":"nonblank"}`,
`{"context":"nonblank","verdict":"reject_and_halt","feedback":"nonblank"}`, and
`{"context":"nonblank","verdict":"reject_and_retry","feedback":"nonblank","payload":"replacement instruction or task array","replacement_context":"optional nonblank replacement context"}`.
The `replacement_context` member is optional only in the final form. Legacy
`reject` and `retry` verdict strings are intentionally incompatible. `accept` is authoritative
and marks its task successful even when a composite's retained nested results
include failures. `reject_and_halt` fails immediately. Only
`reject_and_retry` initiates another attempt; optional nonblank replacement
context replaces the current task context for later attempts and descendants.
When an instruction leaf omits it, the response's required current context is
carried forward. A retry payload may be either an instruction or a task array:
an instruction continues in the same retained leaf session, while a task array
transitions to the composite research turn, nested sibling execution, and a
later validation turn on the retained composite agent, supplying retry context
to its nested child agents.

Leaf mapping is direct: response `context` becomes result context, accepted
`evidence` is serialized as top-level `evidence`, and retry `feedback` is
retained and may be exposed as failure feedback. Leaf `task_patch` and
`execution` are intentionally null or absent because there is no leaf execution
transcript. Composite research/patch, nested execution, and validation fields
remain populated as applicable. The AgentTask v1 artifact envelope and schema
are unchanged.

`agent_tasks.maximum_attempts` is global runtime configuration enforced
independently per task invocation. It accepts any positive `Int32`, defaults to
5, and includes the first payload execution. Composite research runs once per
invocation.
When the final attempt returns `reject_and_retry`, its feedback and replacement
context are retained as the latest effective result, but its replacement payload
does not run and the task fails. Composite payloads rerun their nested sibling
graph on each retry. Large limits and composite retries can repeat costly or
side-effecting work; use a small bound and explicit mutation dependencies.
Ready siblings run in parallel. An unsuccessful dependency blocks only its
descendants while independent branches continue. Results preserve the hierarchy
and include graph/task status, attempts,
research context, effective patch, execution, verdict/evidence, failures or
blocking dependencies, and nested results. Cancellation aborts and joins every
runner-owned child before it propagates. There is deliberately no rollback or
resume: concurrent ready tasks share a workspace, and dependencies are the
planner's only mutation-ordering mechanism. Plans must declare every required
write serialization; the scheduler cannot protect undeclared concurrent writes.

**Progress snapshots.** The server emits an additive `AgentTaskProgressSnapshot`
event for each running `run_agent_tasks` call. Every event is a complete,
monotonically revised tree, preserving effective declaration order. The first
snapshot is emitted before work starts with all nodes pending; later snapshots
are emitted for running, terminal, and blocked transitions and for effective
subtree replacement. Status icons are `○` pending, `◐` running, `✓` succeeded,
`✗` failed, `⊘` blocked, and `■` canceled. A research patch or retry payload
replaces the displayed descendants with the current effective subtree. On
cancellation, the final snapshot is emitted only after runner-owned children
have been joined, and cancellation then propagates.

Both CLI modes append each complete tree to permanent output and flush it; they
do not replace or remove earlier snapshots. The persistent enhanced CLI also
projects the newest matching snapshot into the active `run_agent_tasks` live
row. Before progress arrives that row is neutral and exposes neither a path nor
embedded graph content. The projection is scoped by agent session and origin
tool-call id, accepts only increasing revisions, is sanitized and bounded to the
live-row budget, and is removed by terminal tool lifecycle. Stale, unrelated,
or late snapshots never replace or resurrect it. This live bound does not alter
the complete protocol snapshot or permanent tree. A snapshot is self-contained,
but the stream does not promise replay or resume to clients that were not
listening. These events supplement the unchanged generic `ToolStarted` and
`ToolFinished` lifecycle and unchanged final hierarchical JSON result.

### Image attachments are session-owned structured content

**Input and transcript.** A prompt is an ordered `MessageContentPart` sequence, not a
string with image paths embedded in it. Text and image parts retain their original
order. Image bytes are stored once in the owning user session's attachment store;
the durable transcript stores the artifact reference and immutable inspected
metadata, never base64 image data or a workspace path. A live provider request may
materialize those referenced parts, bounded to 40 MiB of image context and 64 MiB of
outbound JSON.

**Ownership and security.** An attachment belongs to the user session that accepted
its upload. Only that session's agents and provider execution may dereference it
through runtime APIs; agent sessions never receive a capability to another user
session's attachment store. The readable host-filesystem baseline can still expose
artifact paths unless configured `deny_read` rules narrow it. Upload authorization
is admission to the target user session, not a filesystem permission. Artifacts are
removed only with their owning user session.

**Supported input.** PNG, JPEG, GIF, and WebP are accepted. Animation is bounded and
preserved as frames for provider adaptation. Each image is at most 5 MiB encoded,
8,192 pixels in either dimension, 40 megapixels per frame, 100 frames, and 100
megapixels decoded across its frames. A prompt or tool-produced image batch has at
most 16 images and 20 MiB of encoded data in aggregate. Validation inspects the
claimed format and decoded frame metadata before the artifact becomes usable; an
extension or MIME declaration never decides safety.

**Synthetic user images.** A tool that produces an image does not inject opaque
provider-specific text or a private artifact path into an assistant result. The
runtime records a synthetic user message whose ordered content contains the image
artifact reference, so subsequent turns see the same structured convention as an
uploaded user image. The synthetic message is session-scoped, durable, and rendered
as user content; it cannot be used to attach a foreign-session artifact.

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

**Parallel provider batches.** Parallel-safety metadata is owned by each `ITool`,
not inferred from a tool name or from generic shell-command analysis. The
interface defaults to unsafe; a tool must explicitly report an invocation as
safe. `AgentSession` walks each provider batch in order and starts each maximal
consecutive run of safe calls concurrently. An unsafe call is a barrier: it is
not started until the preceding safe run has settled, and the next safe run is
not started until that call has settled. Calls after an interruption, unknown
or malformed invocations, and otherwise unsafe calls are settled as canceled or
errors according to their normal lifecycle rather than being launched as part of
a safe run.

Execution may complete in any order, but settlement is deterministic: results
are appended to durable history and published in provider call order, and that
same order is used for the next provider request. Restored durable settlements
are reused and reconciled with any remaining calls without re-executing them;
the same barriers and ordering apply to the remainder.

`exec_command` is the one configured exception. It is considered parallel-safe
only when its command begins, after shell whitespace, with one of the configured
`read_only_exec_command_prefixes` entries followed by the end of the command or
shell whitespace. The default list is in `predefined_config.yaml`, and user
entries extend or replace it using the normal configuration rules. This is a
lexical prefix check only; Parrot does not attempt general shell analysis and
must treat commands that do not match as unsafe.

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
- **M8.** The mode registry lists and resolves profiles whose independent
  `is_user_selectable` flag is true; the shipped foreground policies are
  `build`, `plan`, and `query`, and their turn hooks remain owned by the mode
  registry. Profile configuration supplies prompts, declared rules, limits, and
  both audience flags. The independent `is_agent_selectable` flag controls
  child-profile listing in the available-subagents prompt and runtime
  `agent_spawn` resolution. A profile may be selectable by both audiences or by
  neither; omitted overrides inherit the predefined values. `default_profile`
  must name a user-selectable profile. Mode remains per-session state rather
  than a YAML key, and neither audience is classified by a fixed profile ID.
- **Prompt templates.** The merged configuration exposes an immutable typed catalogue of stable template IDs. A user may recursively override one template field without copying its argument contract. Named placeholders are parsed once; allowed and required argument sets are explicit, doubled braces escape literals, and runtime values are substituted in one pass so their braces cannot become template syntax. Invalid definitions and render calls report the corresponding `prompt_templates.<id>` path.
- **Tool definitions.** The shipped `predefined_config.yaml` owns each complete
  model-facing tool definition: its description and standard JSON Schema
  `parameters` object. User `config.yaml` recursively layers over descriptions
  and structural schema members. Runtime tools still own deserialization,
  validation, execution, sandbox, permission, and security behavior; configured
  schemas guide the model/provider and cannot grant runtime authority.

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

### `ImageArtifactStore` — rank 2, image support

- **Owns** session-private encoded image artifacts, their content-addressed identity,
  inspected format/frame metadata, and references held by durable transcript parts.
- **Inbound** accept a validated streamed upload; stage and atomically promote its
  artifact; resolve a referenced image only for the owning user session's transcript
  or provider adapter; release it with the owning session. It accepts PNG, JPEG, GIF,
  and WebP subject to the attachment limits stated above.
- **Outbound** the owning `UserSession` private root and `SessionDatabase` for
  metadata/reference transactions.
- **Boundary** no. The upload RPC and provider adapters are its transport adapters;
  callers never receive a general attachment-root path.
- **Note** deduplication may occur only within the owning user session. A digest is
  not an authorization capability and must never make another session's artifact
  discoverable.

### `EventRepository` — rank 2, M2

- **Absorbs** `event` (persistence half).
- **Owns** the durable event log and its query projections, and the transaction
  that commits both together (principle 9).
- **Inbound** append an event, read a range. Guarantees the event and its
  projection commit atomically or not at all.
- **Outbound** `SessionDatabase`.
- **Boundary** no.

### `AgentHistoryTimeline` — rank 3, M8

- **Owns** each agent session's ordered history timeline and JSONL projection in
  that agent's scratch directory.
- **Inbound** record durable message and compaction records after their SQLite
  transaction commits; project the agent's complete timeline atomically enough that
  an interrupted projection can be rebuilt from SQLite.
- **Outbound** `SessionDatabase` as the sole authority for durable history and the
  owning `AgentSession`.
- **Boundary** no. It is a per-agent component, not a user-session-wide history
  service.
- **Note** JSONL is an inspectable projection, never an alternate persistence
  authority. Runtime history APIs remain agent-scoped, while filesystem reads use
  the readable host baseline and any configured `deny_read` rules.
- **Checkpoints and effective history.** A durable `set_checkpoint` record names
  a tool-call group in this agent's conversation. Its title is exact (whitespace-
  only is invalid), and duplicate exact titles use latest-wins semantics.
  Effective history is the current compaction summary, applicable status, and
  retained groups, not the unbounded durable timeline. A checkpoint outside that
  effective material is unavailable for a later fork; compaction can therefore
  make it unavailable without altering the durable record.

### `QueueStore` — rank 3, M8

- **Absorbs** `internal/queue` and the queue portions of upstream `tool`,
  `status`, `agent`, and `session`.
- **Owns** the root agent's durable named JSONL queues and the lifetime-scoped
  stores of child agents, including metadata, lock discipline, and monitored
  delivery state. Listening registrations are per invoking agent, not queue
  metadata shared among consumers.
- **Inbound** explicitly create, inspect, list, push, take, close, listen, and
  offer one item to an idle listener. A tool invocation resolves its own queue
  first and
  then its direct parent's queue. It cannot resolve a child's, sibling's, or
  grandparent's queue. Queue names are canonical lowercase ASCII words joined
  by hyphens; empty root queues remain durable.
- **Outbound** the user session's private root queue directory and the owning
  `AgentSession` for idle notification admission.
- **Boundary** no. Concrete and scoped to an agent owner; the five queue tools
  are its adapters.
- **Note** `queue_push` requires exactly one item source: inline `items`, or a
  workspace-relative or read-authorized absolute `source_file`. Source files are
  UTF-8 text, contribute one item per nonblank line without trimming retained
  text, and are rejected above 16 MiB; the final persisted queue has its own
  separate 16 MiB limit. Direction and close apply to the complete loaded list.
  `queue_push(close:true)` is a producer completion signal whose item list may be
  empty, including after source-file filtering. Repeating an empty closing push
  is idempotent; any other later push is rejected. Closing preserves buffered
  items for draining and allows polling `queue_take` calls to finish promptly
  without waking `queue_listen` or `wait`.
  Every `queue_take` result includes the closed state, including an open empty
  timeout, so consumers terminate only after observing a closed drained queue.
- **Note** a queue name must be unique across a direct parent-child edge in both
  creation orders, so own-first lookup cannot make a collision ambiguous.
  Siblings may reuse a name because neither can access the other's queues.
  Root-owned queues persist with the user session; a child's store and all its
  queues end with that child session.
- **Note** queue files use bounded, strict JSON Lines and lock directories so
  independent store instances/processes share one read-modify-write discipline.
  The external replay-latest inventory contains every non-empty queue in the
  user session with its owning agent-session id, name, description, and item
  count. It emits catalog-wide complete snapshots after durable count changes
  and omits a child's rows when that child ends. Queue contents and internal
  delivery metadata never cross that boundary, and the in-memory revision is
  not durable history or a reconnect cursor.

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

- **Absorbs** the provider configuration and catalogue half of `provider`.
- **Owns** the configured and built-in providers, and the merged model
  catalogue. Built-in serializable provider defaults and offline
  `model_defaults` catalogues are authored in `predefined_config.yaml`;
  implementation-specific adapters and decoders remain code-owned.
- **Inbound** resolve `provider/model` to an `ILLMProvider` and a model; list
  models. The model portion keeps any vendor prefix (split on the first slash),
  so `openrouter/openai/gpt-4o` resolves to provider `openrouter`, model
  `openai/gpt-4o`.
- **Outbound** `Configuration`, `ICredentialStore`.
- **Boundary** no.
- **Note** the catalogue lives on the registry rather than on `ILLMProvider`,
  so a provider stays stateless: it can list models, but remembering them is the
  registry's job. Endpoint metadata takes priority, while explicit `models` and
  offline `model_defaults` fill fields the endpoint omits. After a successful
  refresh, defaults omitted by the endpoint are dropped; explicit models remain
  selectable. User-facing listing checks credentials each time,
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
- **Provider subscription usage is separate.** `IUsageReporter` (optional
  capability) with `SubscriptionUsage`, implemented by ChatGPT, OpenCodeGo, and
  Kimi, reports provider-account subscription information. It is not the
  user-session metered-usage model and is out of scope for that model; it is
  implemented but not yet surfaced in the CLI (upstream shows it in status).
- **Retry + classification.** `ProviderErrors` (`IsUsageLimit`/
  `IsEngineOverloaded`) and `RetryingProvider`, a decorator the registry wraps
  around every provider, folding upstream's header-retry and stream-retry layers.
- **Defaults + build.** Serializable provider defaults and offline
  `model_defaults` catalogues are loaded from `predefined_config.yaml`.
  `ProviderRegistryBuilder` absorbs `app.BuildProviders` (env-var → credential
  key resolution, defaults merge, retry wrapping). `ProviderRegistry` absorbs
  `agent/provider.go`. Implementation-specific adapters, model-list decoders,
  and the ChatGPT OAuth transport remain in code rather than configuration.

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
- **Owns** pending write-permission requests and applies accepted approvals to
  the requesting `AgentSession`'s effective `SecurityProfile`.
- **Inbound** `request_write_permission` requires one or more exact existing
  absolute paths and a nonblank reason. The broker resolves canonical physical
  targets: a file approval is exact-file; a directory approval includes
  descendants. It authorises a **canonical operation**, never a tool name
  (principle 7). When the effective security profile already permits every
  requested target, the tool completes without broker mediation. Only the
  server-declared Grant, Reject, and Reject-with-reason replies are accepted;
  cancelled selection or reason entry is Reject, and a blank required rejection
  reason is invalid. Noninteractive sessions reject immediately and pending
  requests time out. Authorisation stays separate from OS containment
  (principle 8).
- **Outbound** typed permission requests and replies through `EventBroker`; an
  accepted reply updates the requesting agent session's effective security
  profile.
- **Boundary** no. A user-approved allow-write rule enables write, edit, and
  shell access within its target, is runtime-only and nonpersistent, and cannot
  override a read-only profile or explicit static deny. Each agent's scratch
  directory is automatically created, and every agent in the same user session
  can write beneath their shared scratch root; neither mechanism has network
  effect.

### `QuestionBroker` — rank 5, M3

- **Absorbs** `question`.
- **Owns** pending questions and their answers.
- **Inbound** ask the user a structured question, await the answer.
- **Outbound** `EventBroker`.
- **Boundary** no.

### `SystemContextBuilder` — rank 7, M4

- **Absorbs** `systemcontext`, `skill`, `command`.
- **Owns** the typed context sources: configured system-prompt providers, date,
  platform, working directory, project metadata, `AGENTS.md` files, skills, and
  tool guidance.
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
  admitted input, messages, context epoch, todos, goals, tasks, and its queue
  scope. Todos, goals, and queue listening registrations are **owned
  sub-objects**, not user-session-global services.
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
  idle drains, interruptions, and tool rounds do not duplicate it. Before a
  foreground turn completes while direct child agents or shell processes remain
  active, a distinct reminder is likewise appended as durable `system` history.
- **Security selection.** Model, profile metadata, and tool filtering remain
  captured at the turn boundary. Effective security is compiled separately for
  each security-sensitive tool invocation, so an ancestor profile update is
  visible to a later tool call in the same turn while one invocation still uses
  one coherent immutable profile.

### `AgentSession` — todos (ported 2026-07-24)

`TodoCollection` is the session-owned durable todo sub-object. It retains its
storage and `TodoUpdated` protocol infrastructure for persisted state and
replay compatibility. Todo rows and events are scoped by agent session. There
is no replacement public interface for this retained infrastructure.

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
- **Owns** user-session admission, the shared retained-agent budget and cancellation
  boundary, root scope registration, and non-owning canonical status traversal.
  Each agent-scoped `ChildRegistry` exclusively owns that agent's direct-child
  scopes, friendly-name namespace, pending reservations, recursion accounting,
  completion policy, and child lifetime.
- **Inbound** register the root and coordinate session-wide profile, budget, and status services. Child creation and retrieval enter through the owning `ChildRegistry`.
  Friendly names are unique and resolvable only among one caller's direct
  children. `agent_send` can address the sender's direct parent or a descendant
  within the sender's own descendant tree and user session. Descendants use
  relative, slash-separated friendly-name paths such as `child/grandchild`;
  these paths travel only downward, cannot traverse upward, and do not authorize
  arbitrary canonical ids for descendants. Exact canonical session ids remain
  accepted only for the direct parent or direct children, not arbitrary agents
  elsewhere in the user session. For a sender with a registered direct parent,
  the case-sensitive literal `parent`, actual parent id, or actual parent
  friendly name resolves to that parent and takes precedence over a colliding
  direct-child friendly name; direct-child names resolve last. A root therefore
  falls through and may resolve its direct child named `parent`.
- **Outbound** `Configuration`, `AgentSession`.
- **Boundary** no.
- **Security inheritance.** A spawned child restricts its configured profile
  with its runtime parent's current effective `SecurityProfile`. Composition is
  monotonic: child rules may further restrict access but cannot reopen an
  ancestor denial. An approval made before the child is created is therefore in
  that child profile; later parent approvals do not alter existing children or
  siblings. The relationship stays live through nested descendants and is
  branch-local. The user-session scratch root is a mandatory runtime write grant
  applied after this inheritance, so it remains writable to every agent in that
  user session.
- **Note** mutually dependent with `AgentSession`; both rank 9. A scoped
  `ChildRegistry` creates, names, retains, observes, and owns each direct child;
  recursive traversal follows those ownership edges. The user-session registry
  never owns or disposes non-root scopes. Neither registry admits input or waits
  for turns: those operations belong to the retrieved `AgentSession`, keeping one owner for drain concurrency and
  terminal results. `agent_spawn` returns immediately without canceling the child. Each child publishes a durable
  `AgentStarted` event followed by exactly one `AgentFinished` or `AgentFailed`
  event; cancellation is a failure carrying the retained interruption message.
  User-session shutdown cancels and joins every child. Profiles, generic task
  APIs, and the remaining `TaskManager` work stay deferred rather than stubbed.
  M8 adds profile-driven audience selectability: `is_user_selectable` governs
  foreground mode discovery and resolution, while `is_agent_selectable` governs
  child discovery and spawning. Both audiences can be enabled or disabled
  independently; typed observation of the existing child lifecycle remains
  separate.
- **Direct-child status.** `agent_status` resolves exactly one retained direct
  child by its canonical session id or direct-child friendly name; it never
  accepts a parent alias, descendant path, sibling, cousin, or arbitrary
  user-session agent id. Each `AgentSession` owns a concurrency-safe live
  activity record for its retained runtime lifetime. The report gives the
  current or completed request-session duration, distinguishes the current
  provider-request duration from the last completed provider-request duration,
  names an executing tool when present, otherwise gives the elapsed age of the
  latest provider stream activity, and includes only active direct subagents, directly
  owned active shell processes, and the five latest completed reasoning-summary
  or assistant-message entries in emission order. Raw and incomplete reasoning
  is never reported. This observation is model-facing text, not an external
  protocol or restart-durable history contract; the existing `status` tree and
  durable conversation projections remain unchanged.
- **Conversation forks.** `agent_spawn.fork` is optional. An omitted or empty
  value starts the child without parent conversation; `full` copies the parent's
  current effective history (summary, applicable status, and retained groups).
  Any other exact value is a checkpoint title and copies from that checkpoint's
  tool-call group through the completed group immediately before the spawn.
  The current batch is excluded, so a checkpoint made by a same-batch tool call
  cannot be forked until a later batch. Unknown, same-batch, and compacted-away
  checkpoint titles fail. A fork transfers history only: it never transfers
  session ownership, security profile, permission approvals, queues, process control,
  or any other runtime authority.
- **Completion delivery.** Absorbing the terminal notification path from
  `internal/agent`, `internal/tool`, and the former `internal/subagent`
  completion notifier, the spawning parent's `ChildRegistry` snapshots its
  direct-child completion policy and parent endpoint, then admits a bounded,
  trusted terminal notification outside its lock. A child
  execution reports after its durable terminal lifecycle event; an idle child
  parent receives it through its own execution lifecycle so nested completions
  propagate one level at a time. The root admits it as ordinary steering input.
  Unlike upstream's process-wide asynchronous notifier, this registry-scoped
  path is awaited by the terminal child execution and is bounded by the
  user-session registry lifetime. Parent delivery failures are best effort and
  cannot alter the child's retained terminal result.

### `UserSession` — rank 10, M2

- **Absorbs** `session` (`InteractiveOwner`, `InteractiveClaim`), `store`
  (owners, claims).
- **Owns** its private root, the exclusive runtime claim on it, the exact launch
  working-directory binding, the root agent's durable queue store, and the
  `AgentSession`s inside it. Child queue stores belong to child session
  lifetimes rather than the user-session root.
- **Inbound** create a fresh session, resume an exact id, or use default-open
  cardinality semantics for a workspace. The exact launch path is retained for
  execution while canonical identity is used only for matching and claims.
- **Outbound** `SessionDatabase`, `StatePaths`, `Configuration`, and its private
  queue directory.
- **Boundary** no.
- **Note** one claim is held for exactly the duration of `Run`. A live owner is
  never joined or displaced. Fresh roots use `main`; resumed legacy roots retain
  their stored root-agent name.
- **Metered usage.** Lifetime user-session metered usage is durable. Each
  metered stats event and the latest-per-agent usage projection commit in the
  same transaction, so the projection cannot get ahead of or fall behind its
  source event. The projection is keyed by agent session, including child agent
  sessions for as long as they belong to this user session. The user-session
  aggregate is calculated by summing that projection; it is not a separately
  persisted second total, so there is no aggregate row to reconcile after a
  crash or child completion. This durable `SessionUsageSnapshot`/metered usage
  is distinct from EnhancedCli's transient modeline rates.

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
  resolves only profiles with `is_user_selectable: true` as foreground modes,
  while child spawning independently requires `is_agent_selectable: true`.
- Status injection and the direct-active-work completion reminder publish
  distinct small transient protobuf events after their durable system messages
  commit. Prompt text stays in message history and is not duplicated on the
  live wire, so clients render each notification from its typed payload.
- `/mode` and `/modes` select and discover foreground policies. `/status` remains
  deferred because it is a separate user-facing summary, not status-prompt
  injection. Basic and Enhanced render the transient notification independently.
- Profile tool capabilities remain distinct from filesystem sandbox rules.
  `query` and `plan` apply their configured workspace policy, while the plan
  artifact remains owned by its user session. Plan approval dialogs remain
  separate from filesystem isolation.

---

## Tools

### `ITool` — rank 7, M3

- **Absorbs** `tool` (the interface and the builtins).
- **Owns** nothing shared; each tool owns its own arguments and plan, its
  execution. It does not own model-facing
  descriptions.
- **Inbound** plan, execute. **Display differences are methods on the tool,
  never a branch on its id.** `exec_command` accepts optional `name`,
  `yield_after_ms`, and `env`, where `env` is a string-to-string map represented
  internally by concrete `ProcessEnvironmentOverrides`, constructed from
  `IEnumerable<KeyValuePair<string, string>>` (`Empty` when omitted). A
  non-object `env` reports
  `error: Tool argument 'env' must be an object containing string values.`; a
  non-string property reports
  `error: Tool argument 'env' must contain only string values.`; and a name
  that is empty or contains `=` or NUL, or a value containing NUL, reports
  `error: Tool argument 'env' contains an invalid environment value.`
  `interrupt_process` requires `name` and accepts an
  optional integer `signal` from 1 through 64, defaulting to 2 (`SIGINT`). The
  host may reject an in-range number that is not a valid signal at runtime. The
  signal is sent only to the tracked outer wrapper: the outer bubblewrap process
  for a pipe run, or the outer `parrot-pty-attach --bridge` process for a PTY
  run. It is not sent directly to the root shell, foreground process group,
  descendants, or process tree. Success means the kernel accepted delivery. The
  tool returns immediately with `Signal N sent to shell process 'name'.` and does
  not wait, escalate, consume output, or retire the process. If the wrapper survives,
  the process stays reserved and running, and ordinary later completion handles
  its result. User-session lifetime cancellation and disposal still force-kill
  the entire process tree. A yield from `exec_command` returns
  the authoritative typed yielded-process handoff without stopping the process;
  clients do not infer the handoff by parsing ordinary result text. For a normal
  non-PTY pipe run, the handoff contains distinct absolute paths to UTF-8 text
  stdout and stderr files in the owning agent's scratch blob area. The files are
  concurrently readable, and each newly received Parrot chunk is flushed with
  an approximately 100 ms visibility target under normal local load. Child
  process buffering is outside Parrot's control. PTY runs are excluded and have
  no output-file paths. A later completion is delivered to the invoking agent
  through its durable steer queue unless a successful wait claims it.
  Non-yielded executions retain their ordinary terminal result behavior, and
  normal completed-result formatting and overflow notices remain compatible.
- **Generic activity wait.** `wait` pauses the invoking agent for incoming
  activity and returns early for a new message, direct-child completion,
  unclaimed yielded-process completion, or an item from an accessible queue the
  invoker enabled through `queue_listen`. Listening registrations are per
  invoking agent: enabling a queue for one consumer neither enables nor disables
  it for another.
- **Builtin mutations.** `write` creates or replaces one file with exact UTF-8
  content. `edit` performs exact ordinal string replacement; without
  `replace_all` it requires exactly one match, while `replace_all` permits zero
  or more. Both write directly under the active filesystem security profile;
  they do not restore the dropped transactional change machinery.
- **Filesystem policy.** `read`, `glob`, `write`, and `edit` operate
  under the active filesystem security profile. The host root is read-only by
  default; configured `deny_read` rules remain effective, and `allow_write`
  rules grant only their matched paths. Runtime-owned plans and blobs remain
  session-owned capabilities rather than general filesystem authority.
- **Outbound** `PermissionBroker`, the invoking agent session's shell-process
  owner, `ProcessRunner`, `WebFetcher`, the filesystem.
- **Boundary** **yes** — tools. `ToolDefinitionCatalog` pairs every composed
  `ITool` name with its complete configured model-facing definition before
  support and profile/global filtering and before anything is sent to the
  model. It fails closed when tool names are missing, extra, or duplicated.
  Structural schema and descriptions come entirely from merged configuration;
  runtime deserialization, validation, execution, and security remain separate
  authorities and may intentionally differ after a user override.
### `IToolFactory` — rank 7, M3

- **Absorbs** the registry half of `tool`.
- **Owns** how one tool is built: a factory per tool, living for one
  `UserSession` and so able to take it by constructor, yielding one `ITool`
  instance per `AgentSession`. The process-tool factories are assembled inside
  the agent-session scope because they close over that agent's process owner.
- **Inbound** create.
- **Outbound** `Configuration`, and the sessions a tool is constructed with —
  the one place rank 7 names rank 9 and 10, granted deliberately in
  `architecture.md`.
- **Boundary** no — `ITool` is the boundary, and a new tool brings a factory
  with it.
- **Note** a session's tool instances are fixed once built, so there is no
  mutable side and no registry. Each turn filters an immutable `ToolSnapshot`
  view over those instances. Profile/model metadata stays captured for the turn,
  while each security-sensitive invocation compiles one effective security
  snapshot from the live parent chain before executing.

### `ProcessRunner` — rank 6, M3

- **Absorbs** `process`.
- **Owns** OS child execution, output capture, output storage, and the sandbox.
  A per-`AgentSession` shell-process owner owns named run state and delivery. A
  user-session coordinator creates and observes those owners and joins all of
  them at shutdown without exposing process lookup or control.
- **Inbound** `Run(string command, ProcessEnvironmentOverrides environment,
  UserSessionResources resources, SecurityProfile securityProfile,
  CancellationToken cancellationToken)`. An agent-session shell-process owner
  starts named runs through `Start(string? requestedName, string command,
  ProcessEnvironmentOverrides environment, AgentSession agent,
  SecurityProfile securityProfile)`.
  By default the child inherits the complete
  launch environment. Explicit overrides replace inherited child variables and
  become deterministically sorted bubblewrap `--setenv` entries. No runtime
  environment variables are protected, cleared, or forced. **Fails closed**: no
  sandbox, no execution. Not a warning, not a fallback. The sandbox provides
  filesystem and process isolation; environment selection remains command
  execution configuration. The host root is read-only by default. Configured
  `allow_write` rules grant writes to matched paths, including the predefined
  shared grants for `/dev/null`, `/tmp`, `${XDG_CACHE_HOME:-${HOME}/.cache}`,
  and the NuGet, npm, and pnpm caches; those predefined grants apply even to
  read-only profiles. Ordinary top-level and profile `sandbox_rules` entries
  append after inherited rules, so adding a rule retains those shared grants. An
  explicit `!replace` sandbox-rule sequence instead replaces its inherited list.
  A missing `allow_write` directory is omitted unless it sets
  `create_if_not_exist: true`, which recursively creates it before granting it.
  Each process additionally receives the user session's automatically created
  scratch root as a writable location, including under a read-only profile. Each
  agent retains an individual directory beneath that root for its own history,
  output blobs, and plans, but parent, child, and sibling agents in the same user
  session can write those directories. This mandatory runtime grant is applied
  after inherited profile restrictions; it does not extend to another user
  session or non-scratch session infrastructure. Parrot does not override
  `HOME`, `XDG_CACHE_HOME`, or `TMPDIR`. The rest of the host remains read-only.
  Stdout and stderr retain at most 65,536 characters each in memory. Every
  normal-pipe run retains all decoded output in separate UTF-8 text stdout and
  stderr files in the owning agent's scratch blob directory; those files persist
  after completion and across session resume. If either completed stream exceeds that
  bound, the complete result may additionally be persisted as the legacy
  formatted blob and the compatible result reports its full absolute path. This
  retention deliberately costs disk space; quotas and garbage collection are
  not included. The readable host baseline also does not make scratch contents
  confidential from filesystem reads.
  Process names are ordinal and unique among running processes within their
  owning agent session: supplied duplicates fail before launch while the current
  binding is running, completed bindings can be replaced atomically, and omitted
  names are generated and reserved atomically. A completed binding remains the
  current lookup target until it is replaced, subject to the existing wait and
  delivery claim rules. Wait and interrupt can address only that agent's current
  binding, while the owner retains every launched process for settlement.
  User-session-wide active-work observations qualify repeated local names with
  the owning agent session id. The user-session coordinator derives complete,
  authoritative active-process snapshots from the live in-memory owners; these
  snapshots are presentation state, not durable database history. Every stream
  subscription receives an initial snapshot, including an empty one, and later
  revisions replace the client's inventory wholesale. Immutable process
  identities and owner generations prevent a completed run from being confused
  with a replacement that reuses its local name. Runs use the user-session
  lifetime token, survive tool-call yield and cancellation, and all per-agent
  owners are cancelled and joined when that user session is disposed.
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

### `AttachmentUploadProtocol` — rank 11, image support

- **Owns** no image state. It translates client-streamed `AttachmentUploadFrame`
  messages into one `ImageArtifactStore` admission and returns an
  `AttachmentUploadResponse` reference for later `MessageContentPart` use.
- **Inbound** `UploadAttachment` is client-streaming. Frames carry at most 1 MiB;
  the protocol rejects malformed ordering, a missing final description, a foreign
  session, or any aggregate that cannot satisfy the image limits before accepting an
  artifact. Retrying after an interrupted stream is a new upload unless its admitted
  artifact reference was received.
- **Outbound** `UserSession` admission and `ImageArtifactStore`. It does not expose
  an artifact filesystem path, bytes from another session, or a cross-session lookup.
- **Boundary** yes — the wire upload surface. `SendMessage` accepts structured,
  ordered `MessageContentPart` values referencing successful uploads, not base64
  images embedded in text.

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
  `UserSession` merges the live-only agent broker with independent replay-latest
  queue and active-process inventory feeds. The queue feed is a catalog-wide
  replacement inventory whose rows retain their owning agent-session ids, so
  child-owned queues attach to the same client hierarchy as agent activity.
  Every listener receives complete initial inventories, including explicit
  empty snapshots. Metered usage is likewise delivered as a complete,
  revisioned `Listen` snapshot: it is a transient latest-state projection, not
  a durable event-stream cursor. Its snapshot includes the root agent and all
  currently retained child-agent usage belonging to the user session; child
  completion does not remove lifetime usage. A reconnect receives a new
  complete snapshot and replaces its local usage state rather than resuming
  from a prior revision. The active-process
  snapshot is rebuilt from authoritative in-memory shell-process owners rather
  than persisted history, so reconnect replaces client state with what is still
  running. Neither inventory changes `EventBroker` into a historical replay
  mechanism.
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

### `ImageInputSyntax` — rank 13, image support

- **Owns** parsing the interactive CLI's local image references before prompt
  admission. `@path` attaches one path without spaces; `@{path with spaces}`
  attaches the enclosed path; and `@@` emits one literal `@`. These forms are local
  input syntax, not text sent to the model.
- **Inbound** interactive and one-shot prompt text. Paths are read under the active
  filesystem security profile, uploaded through `UploadAttachment`, and replaced by
  the returned image `MessageContentPart` at the same position in the ordered input.
  An unreadable, invalid, or over-limit image rejects the submission rather than
  silently sending its spelling as ordinary text.
- **Outbound** `AttachmentUploadProtocol` and `SendMessage` structured content.
- **Boundary** no. Basic and Enhanced each adapt the same syntax to their own input
  mechanics; neither stores image bytes or interprets private artifact paths.

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
  promotion of stable rows into ordinary terminal scrollback. It also owns a
  persistent inventory-row layer between transient turn content and the
  modeline/editor. Queue rows use fixed retention and survive turn redraws;
  animated process rows are keyed by immutable process identity and remain until
  an authoritative snapshot reports actual exit, not merely until the launching
  tool call yields.
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
- **Note** enhanced mode starts its sole stream consumer immediately and keeps
  it active while idle and across turns. Queue and active-process snapshots are
  demultiplexed before turn rendering, so they cannot stop thinking animation,
  split text or reasoning, establish foreground hierarchy, or enter
  activity/scrollback. Each full process snapshot replaces local process state;
  therefore reconnect both restores surviving rows and removes stale ones.
  Complete physical assistant rows become immutable scrollback while
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
  Highlighting is bounded at 512 KiB and 10,000 lines. The modeline's `i/s` and
  `o/s` retain exact provider-reported input/output totals only when this local
  client receives a provider completion; cached input is already part of input.
  Their 30 one-second buckets always normalize retained totals by 30, including
  before the window is full. These completion-receipt samples are local,
  transient, and neither persisted nor replayed: a late attach, reconnect, or
  session replacement begins without a prior rate and sees only subsequent
  completions. They are not provider-side token-generation timing and are
  separate from durable `SessionUsageSnapshot` metered usage. Picker, modal
  prompts, and richer multi-row activity frames remain deferred M7 work.

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
