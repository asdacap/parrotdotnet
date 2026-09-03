# Parrot

Parrot is a local coding agent built with .NET Native AOT. Run it interactively,
send it a one-shot prompt, or host it as a service for remote clients. It
supports configurable model providers, persistent sessions, foreground modes,
and delegated child agents.

## Why Native AOT

Parrot ships as a self-contained executable that starts quickly and does not
require a shared .NET framework. Native AOT is therefore a hard requirement
rather than an optimisation: there is no JIT warm-up, and startup takes only a
few milliseconds. Native AOT constrains the code that can be written — no
runtime reflection over unannotated types, no `Assembly.Load`, no unbounded
generic virtual dispatch — and those constraints are enforced at build time by
the analyzers in `.editorconfig`, not discovered at publish time.

## Layout

```text
Parrot.slnx
src/
  Parrot.Core/        Everything that is not the entry point. Components are
                      organized as namespaces here (Parrot.Agent, Parrot.Tool,
                      ...), not separate assemblies.
  Parrot.Cli/         The `parrot` executable. AOT-published.
test/
  Parrot.Cli.Tests/   TUnit. One test project per src project.
docs/
```

Assemblies are few on purpose: build time and AOT link time both scale with
project count, and boundaries between components are enforced by namespace
discipline and analyzer rules rather than by `ProjectReference` graphs. A new
assembly should have a clear architectural reason.

## Model Selection

A model selection is either a configured model alias or a canonical
`provider/model[/effort-variant]` selector. The provider is the first path
segment, and the model may itself contain slashes. The refreshed provider
catalog supplies autocomplete and model metadata. Endpoint-provided metadata
has priority; configured `models` and `model_defaults` fill only fields the
endpoint omits. Declared `models` are always selectable, even when an endpoint
does not list them. Offline `model_defaults` catalogues seed model names and
descriptions, but a successful endpoint refresh drops entries that the endpoint
omits. An
undeclared model ID is still passed to the provider and can fail when called.
The optional final segment is treated as an effort variant only when the
complete remainder is not an exact catalog model ID. This makes selectors
stable even when a provider offers slash-containing model IDs.

A variant has a stable catalog name and a provider-facing `reasoning_effort`
value. They are intentionally distinct: selecting `high`, for example, can map
to the provider effort `xhigh`. A bare `provider/model` leaves reasoning effort
unset so the provider chooses its default. Interactive `/model` preserves a
compatible current effort when possible; otherwise it uses the target model's
first listed variant, or clears the effort for a model without variants.

`/model` and `/effort` persist the complete requested selector through the
shared configuration. `/effort NAME` selects one of the active model's listed
variants; bare `/effort` presents those variants in provider order. `--model`
is per invocation and accepts an alias or the same complete canonical selector.
`--variant NAME` is a deprecated startup-only override: it resolves an alias
first, then replaces the selected canonical suffix after validation against that
model; it does not persist.

The gRPC model-list response carries ordered model-variant metadata
additively. Each entry contains the stable `name` and mapped
`reasoning_effort`; session state remains the single canonical model selector,
not a separate variant field.

Provider request dialects deliberately differ. Responses requests place a
selected effort and automatic summary under
`"reasoning":{"effort":"…","summary":"auto"}`. Chat Completions requests
place it at top level as `"reasoning_effort":"…"`. Both omit their effort field
for a bare model selection.

## Configuration

### Prompt templates

`prompt_templates` is a typed catalogue of stable, named model-facing templates. User configuration recursively overrides individual fields, so replacing only `prompt_templates.<id>.template` preserves its predefined argument declarations. Placeholders are named (`{argument}`), must be declared in `allowed_arguments`, and every name in `required_arguments` must be supplied when rendering. `{{` and `}}` produce literal braces. Substituted runtime values are inserted in one pass, so braces within those values remain literal.

Malformed templates, unknown or repeated placeholders, undeclared or duplicate render arguments, and missing required arguments are rejected with the relevant `prompt_templates.<id>` configuration path.

Parrot writes an agent-readable `predefined_config.yaml` alongside the
user-owned `config.yaml`. The user file is recursively layered over the
predefined defaults and is never rewritten except by an interactive setting.

`live_buffer_rows` must be a positive integer. Its predefined default is `20`; it
budgets only the enhanced CLI's transient Tail rows, excluding the fixed
inventory/modeline rows and caret-retained input rows.

Top-level `system_prompts` is a mapping from namespaced system-provider keys to
nonblank prompt strings. Its entries configure static system-prompt providers;
for example:

```yaml
system_prompts:
  runtime:system-context:01-base: Additional base guidance
  custom:workflow: Guidance from a custom provider
```

Nested user mappings override a predefined entry with the same provider key and
add new keys without replacing unrelated entries. Provider keys must follow the
same namespaced key rules as runtime system-prompt providers. A configured key
that collides with a runtime provider is rejected rather than replacing the
runtime provider. This map is separate from `profiles.<id>.prompt`, which is
mode-specific guidance, and `model_augment_system_prompts`, which augments the
prompt for selected model selectors.

Profiles are configured under `profiles`. Each profile has two independent
selectability flags: `is_user_selectable` controls foreground mode listing and
mode resolution, while `is_agent_selectable` controls the available-subagent
prompt and `agent_spawn.agent` resolution. Neither flag classifies a profile by
fixed ID, and a profile may be selectable by both audiences or by neither.
Omitted profile fields inherit the values from the predefined configuration, so
these flags can be changed independently with partial overrides. The shipped
configuration makes `build`, `plan`, and `query` user-selectable modes, and
makes `explorer`, `review`, `worker`, `thinker`, `agent-task-prepare`,
`agent-task-payload`, and `agent-task-validation` agent-selectable children.
The `agent-task-*` profiles are used internally by `run_agent_tasks`: composite
work uses distinct preparation and validation turns on one retained composite agent,
which owns nested child agents, while instruction leaves use the payload profile
for combined implementation and verification. The preparation phase builds context
and may optionally apply a narrow run-local patch to the effective inner task graph;
research is only one possible preparation activity. The published identifiers are
`agent-task-prepare` and `agent-task.prepare`. This is an intentional breaking
configuration-key rename from `agent-task-pre-hook` and `agent-task.research`;
existing custom configuration must rename those keys because compatibility aliases
are not provided. `default_profile` must name a user-selectable profile and is used
when no mode is selected explicitly. The foreground-mode RPC and slash-command
surfaces list only user-selectable profiles; child-agent prompts and spawning
accept only agent-selectable profiles.

Every profile has a nonempty `prompt` and `usage`; a positive `max_turns`; a
nonnegative `recursion_limit`; boolean `read_only`; boolean
`enforce_active_work_completion`; boolean `is_user_selectable`; boolean
`is_agent_selectable`; and optional ordered `sandbox_rules`.
`enforce_active_work_completion` controls whether the runtime requires active
work to be completed before the profile may finish a turn. The prompt is the
profile's only model-facing guidance. Because
configuration scalars replace rather than merge, overriding a profile prompt
replaces the complete predefined prompt, including its default behavioral
instructions; there is no separate `hard_rules` field to inherit or override.
Runtime restrictions such as `read_only`, tool filtering, and sandbox rules
remain independently enforced and are not weakened by prompt text. A child
profile can recur only up to its selected profile's recursion limit. Profile
sandbox rules append to that profile's predefined list; top-level
`sandbox_rules` apply to every profile.

A spawned child inherits its runtime parent's current effective security
profile and may only narrow it with the selected child profile; a child allow
cannot reopen access denied by an ancestor. Existing descendants recompile that
chain for each security-sensitive tool invocation, including between tool calls
in one model turn, while each individual invocation uses one immutable compiled
snapshot. Child-local restrictions remain within their branch.

```yaml
default_profile: build
profiles:
  worker:
    usage: Delegate independently scoped implementation work.
    max_turns: 64
    allowed_tools:
      - read
      - edit
```

`allowed_tools` replaces its predefined sequence rather than appending. It has
three meanings: omit it (or use `null`) to retain every otherwise available
tool, use `[]` to offer none, or list exact tool IDs to offer only those tools.
The same filtered set is both sent to the model and used for execution.
`explore` is accepted as a compatibility alias for the canonical `explorer`
child profile.

`disabled_tools` is a global mapping keyed by exact tool ID. A `true` value
disables that tool for every profile, even when the profile lists it in
`allowed_tools`. A `false` value re-enables an entry set to `true` by a
lower-precedence configuration layer. Only entries whose effective value is
`true` are disabled.

```yaml
disabled_tools:
  web_fetch: true
```

CLI executable discovery is configured with ordered `cli_utilities.expected`
and `cli_utilities.optional` sequences. User entries append after their
respective predefined defaults. Use `!replace` on either sequence to replace
its inherited entries, including `!replace []` to clear it. Appending preserves
duplicates; the effective list must still satisfy the field's uniqueness
validation. Names must be nonempty executable basenames without whitespace or
path separators and must be unique within each effective sequence. A name may
occur in both sequences; in that case the expected classification wins. Both
keys are required in the effective merged configuration.

```yaml
cli_utilities:
  expected:
    - git
    - rg
  optional:
    - docker
    - dotnet
```

Sandbox configuration is an ordered list of `sandbox_rules` at the top level
and optionally on each profile. Each item has an absolute `path`, a `rule`
(`allow_write`, `allow_read`, `deny_write`, or `deny_read`), and may set
`create_if_not_exist` for an `allow_write` directory. User rules append after
predefined rules, preserving duplicates and declaration order. Use `!replace`
on a sandbox-rule sequence to replace inherited rules, including `!replace []`
to clear it. Matching rules are applied from broader paths to more specific
paths, so the most specific match wins; for the same normalized path, the later
rule wins. Top-level rules apply to every profile; profile rules append after
that profile's predefined list. A path may use `${NAME}` to require a nonempty environment variable or
`${NAME:-fallback}` to use a fallback when the variable is unset or empty;
fallbacks may themselves use expansions. Expansion happens when configuration
is loaded, without shell evaluation, and the result must be a fully qualified
path. An unavailable required variable or invalid result rejects the
configuration.

The host root is the read-only baseline. Configured `deny_read` rules remain in
effect and can narrow that baseline. `allow_write` rules grant only their
matched paths; a missing `allow_write` path is omitted unless its
`create_if_not_exist` value is `true`, in which case Parrot recursively creates
the missing directory before granting it. `create_if_not_exist` applies only to
`allow_write` directories.

Predefined shared write grants cover `/dev/null`, `/tmp`,
`${XDG_CACHE_HOME:-${HOME}/.cache}`, and the NuGet, npm, and pnpm caches. These
grants apply even to `read_only`
profiles. Filesystem access does not grant network access.

Each agent receives an individually owned scratch directory beneath its user
session. It is automatically created for that agent's history projection,
process and tool output blobs, and plan artifacts. Every agent in the same user
session can write anywhere beneath the shared scratch root, including when it
uses a read-only profile (provided the profile exposes a shell tool). This grant
does not include another user session or non-scratch session infrastructure.
Parrot does not override `HOME`, `XDG_CACHE_HOME`, or `TMPDIR`, and the read-only
host baseline does not hide scratch contents from filesystem reads.

Legacy `profiles.<id>.status` input is accepted and ignored for compatibility.
It is not profile guidance and is never injected into a prompt.

## Approved AgentTask workflow

Plan mode produces a correlated pair of private, runtime-designated artifacts:
a human-readable Markdown plan and an AgentTask JSON artifact. It completes only
when both files are nonblank and the JSON validates. The `PlanCompleted` dialog
presents the Markdown followed by the validated approved pending task hierarchy
for approval. This is the approved declaration before execution, not one of the
later `AgentTaskProgressSnapshot` execution trees: preparation patches and retry
payloads can replace a run's effective subtree without changing that approved
hierarchy. Choosing implementation changes to build mode with both approved
paths and directs it to call `run_agent_tasks` with the JSON path. The tool
reopens and validates that regular, non-symbolic-link file at invocation time;
approval does not make a later changed artifact trusted. This approved workflow
remains path-based even though callers may also submit an embedded artifact
directly.

The v1 artifact has this strict envelope:

```json
{
  "schema_version": 1,
  "tasks": [
    {
      "name": "compile",
      "description": "Build the approved change.",
      "payload": "Implement and verify the change.",
      "acceptance_criteria": "The focused build succeeds.",
      "dependencies": [],
      "model": "provider/model"
    }
  ]
}
```

`schema_version` must be `1`, and the artifact `tasks` array must contain exactly
one top-level root task. Every task requires nonblank `name`, `description`,
`payload`, and `acceptance_criteria`; `model` and `dependencies` are optional. A
payload is either a nonblank instruction or a nonempty recursive sibling task
array; nested sibling arrays may contain multiple tasks. Unknown fields and
null required values are rejected. Names and dependencies are case-sensitive.
Dependencies are distinct, must name another task in the same sibling list, and
may not be self-references or cycles; a task cannot depend on a nested task or a
task in another branch. This is a compatibility change from earlier artifacts:
wrap prior multiple roots in one composite parent, placing them in its nested
payload, so one retained composite agent can prepare and validate the entire
flow.

`run_agent_tasks` requires exactly one graph source. A `path` names a readable
regular non-symbolic-link artifact and is checked against the invoking security
profile before it is reopened and parsed. Alternatively, `artifact` embeds the
v1 object directly; embedded input is parsed in memory and performs no
filesystem read or read-permission check:

```json
{
  "artifact": {
    "schema_version": 1,
    "tasks": [
      {
        "name": "compile",
        "description": "Build the approved change.",
        "payload": "Implement and verify the change.",
        "acceptance_criteria": "The focused build succeeds."
      }
    ]
  }
}
```

Both forms enter the same strict AgentTask parser, and supplying both or neither
is rejected. Plan-approved builds continue to use the path form so their
invocation-time file and security checks are preserved.

`run_agent_tasks` runs the graph synchronously. Fresh retained-only children do
not inherit conversational context, but use the same workspace and normal
user-session-scoped runtime resources. Composite tasks use one retained composite agent for distinct preparation and
validation turns, and that agent owns the recursively executed nested child
agents. A fresh instruction leaf uses one fresh `agent-task-payload` child: that
child implements and verifies the instruction and is retained for the whole leaf
invocation. It has no inherited
conversation. On each retry the same retained session receives a new user prompt
while its previous exchange remains retained; therefore its non-system message
count grows as 1, 3, 5, ... across attempts. The leaf response is parsed
directly, rather than producing a separate execution transcript. Internal
completions never steer the invoking agent. A task's `model`, when present, is
routed through normal model resolution; otherwise the selected child inherits the
invoking turn's requested model.

Composite tasks begin with a mandatory preparation phase. It returns strict JSON
with nonblank `context` and may omit `task_patch`; when supplied, the patch is
sparse and may replace only `description`, `payload`, `acceptance_criteria`, or
`model`. Omitted fields remain unchanged. The patch is validated for that run
only and never writes back to the approved artifact. Descendants receive the
ordered root-to-parent ancestor declarations and root-to-current preparation
contexts, each labelled with its task path. They never receive sibling or cousin
preparation context. Direct dependency summaries are also supplied to a ready task.

The retained composite agent then reviews its nested result in a distinct
validation turn, using the existing strict acceptance forms; nested task agents
remain children owned by that composite agent. An instruction leaf response must return JSON with nonblank `result` and exactly one
strict verdict: `accept` with nonblank evidence, `reject_and_halt` with nonblank
feedback, or `reject_and_retry` with nonblank feedback and a replacement
instruction string or task array. The strict leaf response forms are:

```json
{"result":"nonblank","verdict":"accept","evidence":"nonblank"}
{"result":"nonblank","verdict":"reject_and_halt","feedback":"nonblank"}
{"result":"nonblank","verdict":"reject_and_retry","feedback":"nonblank","payload":"replacement instruction or task array","replacement_result":"optional nonblank replacement result"}
```

`result` is required and nonblank on every leaf response. The
`replacement_result` member is optional and permitted only on the
`reject_and_retry` form. The legacy `reject` and `retry` verdict strings and old
leaf `context`/`replacement_context` members are intentionally incompatible. An
`accept` verdict is authoritative: it succeeds even if a composite task's nested
result contains failures, which remain visible in the result. `reject_and_halt`
fails immediately. Only `reject_and_retry` initiates another attempt; optional
`replacement_result` replaces the result carried into later attempts and direct
dependents. When omitted, the response's result is carried forward. A retry may
replace the payload with another instruction or task array. An instruction
replacement continues in the same retained leaf session. A task-array
replacement transitions to composite preparation, nested execution, and later
validation; composite preparation context remains separate.

For a leaf, response `result` becomes the serialized top-level `result`, accepted
`evidence` remains validation evidence, and retry feedback is retained as failure
feedback when applicable. Leaf `context` is null: preparation context belongs only
to composite lifecycle. Leaf `task_patch` and `execution` are null or absent
because there is no leaf execution transcript. Composite results retain their
preparation context/patch, nested execution, validation fields, and nested child
results. Direct dependents receive only each declared dependency's bounded
`result` (with existing fallbacks when no result exists), preserving declaration
order and blocking semantics. The AgentTask v1 artifact envelope and schema above
are unchanged.

`agent_tasks.maximum_attempts` is global runtime configuration enforced
independently for every task invocation. It accepts any positive `Int32`,
defaults to 5, and includes the first payload execution. Composite preparation runs
once per invocation, not once per retry. If the final attempt returns
`reject_and_retry`, its feedback and replacement context remain on the latest
composite result, but no replacement payload runs and the task fails. Composite
payloads recursively rerun their sibling graph on each retry. Large limits and
composite retries can repeat costly or side-effecting work; choose a small bound
and declare dependencies for mutation ordering.

Ready sibling tasks run concurrently. A failed, blocked, or canceled dependency
blocks only its descendants; independent siblings continue. The returned JSON
is a hierarchical result: graph and per-task statuses, attempt count, preparation
context, any run-local patch, execution, verdict/evidence, failure or blocking
dependencies, and nested task results. Cancellation stops runner-owned children
and waits for them to finish before cancellation propagates. This workflow has
no rollback and no resume facility. Parallel tasks share one workspace, so the
planner must express dependencies for any mutation ordering; the scheduler
cannot make undeclared concurrent writes safe.

While the graph runs, the server also emits additive `AgentTaskProgressSnapshot`
events. Each event is a complete, ordered tree for one `run_agent_tasks` call,
not a delta: an initial snapshot contains every task as pending, and subsequent
snapshots are emitted when tasks become running, reach a terminal state, become
blocked, or their effective subtree changes. The status icons are `○` pending,
`◐` running, `✓` succeeded, `✗` failed, `⊘` blocked, and `■` canceled. Preparation
patches and retry payloads replace the displayed descendants with the current
effective subtree, so stale attempt descendants are not retained. Cancellation
publishes a final snapshot after runner-owned children have been joined, then
propagates cancellation.

Both CLI modes append and flush each complete snapshot as permanent output;
that history is not replaced or removed. In addition, the persistent enhanced
CLI projects the latest snapshot into the matching active `run_agent_tasks`
live row. The row begins with a neutral label that exposes neither the path nor
an embedded artifact, then becomes a bounded, sanitized tree when progress
arrives. Only strictly newer revisions for the same agent session and origin
tool-call id update it; terminal cleanup removes it, and stale or late events do
not resurrect it. Live truncation does not change the complete snapshot or its
permanent tree. Snapshots are self-contained, but the stream provides no replay
or resume guarantee for a client that was not listening. The ordinary
`ToolStarted`/`ToolFinished` lifecycle and the final hierarchical JSON result
are unchanged; progress snapshots supplement them.

## Image attachments

Parrot accepts PNG, JPEG, GIF, and WebP image attachments, including bounded
animation. An image may be up to 5 MiB encoded, 8,192 pixels on either axis, 40
megapixels per frame, 100 frames, and 100 megapixels decoded across all frames. A
prompt or tool image batch may contain at most 16 images and 20 MiB encoded in total.
Provider adaptation is additionally bounded to 40 MiB of live image context and 64
MiB of outbound JSON.

The client uploads an image through the client-streaming `UploadAttachment` RPC.
Each `AttachmentUploadFrame` is at most 1 MiB; `AttachmentUploadResponse` returns a
session-scoped artifact reference. A sent prompt is an ordered sequence of structured
`MessageContentPart` text and image values, so transcript order is preserved without
embedding base64 image data in prompt text. The image bytes and inspected metadata
live in the owning user session's attachment store. Runtime artifact references
remain session-scoped, while direct filesystem reads follow the host-readable
baseline and any configured `deny_read` rules. An artifact reference or digest
does not authorize another session to resolve it through runtime APIs.

In either CLI, `@path` attaches an image at a path without spaces, and
`@{path with spaces}` attaches an enclosed path. Use `@@` for one literal `@`.
These are local input forms: the CLI reads the permitted path, uploads it, and sends
an image content part in its place. Invalid, unreadable, unsupported, or over-limit
images reject the submission rather than being silently sent as text. Tool-produced
images enter later context as synthetic user image parts, never as a private path or
provider-specific text.

## Write permission requests

`request_write_permission` is the only way an agent can ask the user to extend
its effective security profile at runtime. A request contains one or more exact,
existing absolute paths and a nonblank reason. The server resolves each path to
its canonical physical target: an existing-file approval applies only to that
file, while an existing-directory approval applies to that directory and its
descendants. A request does not authorise a tool name, a profile, or an unbounded
part of the filesystem. If the effective security profile already permits every
requested target, the request completes immediately without a user prompt.

The CLI offers only the server-declared choices: **Grant**, **Reject**, and
**Reject with reason**. Cancelling the picker or reason entry is a plain reject;
a blank required rejection reason is invalid. Noninteractive sessions reject
requests immediately, and unanswered pending requests time out. A rejection
leaves the sandbox unchanged.

An approval adds an allow-write rule to the requesting agent session's effective
security profile. It allows writing through the filesystem tools, including
write, edit, and shell, within the approved target; it is not written to
configuration and does not survive the session. A child security profile is
restricted from its parent's current effective profile, so an approval made
before the child is created is inherited, while later parent approvals do not
change an existing child or sibling. An approval cannot override a read-only
profile or an explicit static deny. Filesystem permission still does not imply
network permission.

## User Session Isolation

A user session is Parrot's unit of private storage, runtime ownership, and child
agent containment. It has one private root beneath Parrot's state directory for
its database, queues, plans, process-output blobs, and internal artifacts. Those
artifacts are never placed in the workspace. At most one active runtime owns a
user session and writes its database; child agents remain within that same user
session and cannot acquire another session's state.

The workspace is the exact working directory from which the session was
launched. Parrot retains that path for execution and agent context. It uses a
canonical or physical identity only to compare and claim workspaces; canonical
resolution does not silently replace the launch path. A workspace is shared
context and is not a place for Parrot's private artifacts.

Session admission has three intentionally different forms:

- **Fresh create** always creates a new user session with root agent `main`.
- **Exact resume** opens only the requested existing session and fails if it
  cannot safely acquire that session.
- **Default open** creates when there is no matching session, resumes when
  exactly one matching session can be selected, and rejects ambiguity rather
  than picking one arbitrarily.

A live owner is never joined or displaced. Resuming an older session preserves
its stored root-agent name instead of rewriting compatibility data. Session
listing is a server-authoritative management operation when connected to a
server; management callers read metadata through `SessionCatalog` and never
obtain a live session, database, queue, or repository.

Each agent also has an inspectable history timeline projection in its scratch
directory. SQLite remains the authoritative durable history: the JSON Lines file
is rebuilt from it rather than becoming a second source of truth. Its records
include durable messages and compactions, so the projection describes both the
conversation and the history cutoffs that reshape later context. Filesystem reads
follow the host-wide readable baseline and any configured `deny_read` rules;
agent ownership still governs runtime history APIs and writes.

### Conversation checkpoints and child forks

`set_checkpoint` durably names a point in the calling agent's conversation for a
later `agent_spawn` fork. Its required `title` is preserved exactly; a whitespace-
only title is invalid. Reusing a title is allowed, and the most recently recorded
checkpoint with that exact title wins.

`agent_spawn.fork` is an optional string. Omitting it, passing `""`, or passing
`"empty"` gives the child no parent conversation. `"full"` gives it the parent's
current **effective history**: the current compaction summary, status when present,
and retained conversation tail. Any other value is a checkpoint title. A named fork
contains the checkpoint's tool-call group and every later *completed* group before
the spawn; the current spawn group is incomplete and is excluded. Thus another tool
call in the same provider batch cannot use a checkpoint created by that batch.
Unknown titles, checkpoints no longer represented by the effective history, and
same-batch checkpoints fail rather than silently selecting a different range.

Compaction reshapes effective history. It preserves only the summary, applicable
status, and retained tail, so a checkpoint compacted out of that material is no
longer forkable. A fork copies conversation context only: it does not transfer the
parent's session identity, security profile, permission approvals, queues, process control,
or any other runtime authority.

Queues are agent-owned within this boundary rather than globally shared by all
agents in the user session. An agent resolves its own queues first and may also
access only its direct parent's queues; a parent cannot access a child's queue,
a sibling cannot access another sibling's queue, and a grandchild cannot reach
the root's queues directly. Queue names must be unique across each direct
parent-child edge regardless of which endpoint creates the queue first. Siblings
may reuse a name because their ownership scopes do not overlap. Root-agent
queues persist with the user session, while a child-owned queue is removed when
that child session ends. `queue_push` accepts exactly one item source: inline
`items`, or `source_file`, a workspace-relative or read-authorized absolute UTF-8
text file. File-backed pushes skip empty and whitespace-only lines, preserve all
other line text as individual items, and reject source files larger than 16 MiB;
the final persisted queue retains its separate 16 MiB limit. Direction and close
apply to the complete loaded list. A producer declares that no more items will
arrive by calling `queue_push(close:true)`; inline `items` may be empty when
closing, and a source file may yield no items after filtering. A repeated empty
closing push is idempotent; any other later push is rejected. Closing retains
existing items for draining and allows polling `queue_take` calls to
finish promptly without waking `queue_listen` or `wait`. `queue_take` always
reports whether the queue is closed,
so an open empty timeout is distinguishable from completion. Live
clients receive a complete non-empty queue
inventory for the whole user session; each row carries its owner session id so
hierarchical interfaces can place it with that agent without broadening queue
access.

## Service Transport

Local mode uses the in-process gRPC contract and binds no socket. When Parrot is
hosted, its default control transport is a Unix-domain socket in a user-only
control directory. The directory is mode `0700` and the socket is mode `0600`,
so another local user cannot connect through filesystem access.

TCP serving is explicit and authenticated; there is no unauthenticated TCP
fallback. Plaintext TCP beyond loopback additionally requires an explicit unsafe
acknowledgement and emits a warning. It is intended only for a deployment where
a secure proxy supplies transport protection. Authentication controls admission
to the service but does not weaken the per-user-session ownership checks.

## Model Aliases

Model aliases give stable names to model selectors. The effective configuration
combines these four predefined aliases with the `model_aliases` map in the
configuration file: `low_llm`, `medium_llm`, `high_llm`, and `xhigh_llm`.
Their predefined `usage` values are:

- `low_llm`: `Explicit reversible mechanical or evidence work with failure-specific validation; never judgmental review.`
- `medium_llm`: `Settled component work requiring local judgment.`
- `high_llm`: `Tactical ambiguity, debugging, coordination, integration, or substantive review.`
- `xhigh_llm`: `Strategic, architectural, open-ended, tightly coupled, difficult-to-verify, or consequential work with hard-to-detect errors.`

The ordinary predefined `model_aliases` targets remain empty. Consequently each
unconfigured alias produces this startup warning until it is configured:
`warning: model alias "NAME" is not configured. Use /model-alias to configure.`

Built-in provider alias targets are also kept in `predefined_config.yaml` as
serializable defaults. They are a separate, opt-in complete set of targets:
every provider entry maps all four predefined aliases (`low_llm`, `medium_llm`,
`high_llm`, and `xhigh_llm`). They do not implicitly set ordinary aliases;
they are applied only when chosen through `/model-alias`.

A configured entry can override any predefined field without losing its default
metadata, and can add another alias. Provider model metadata follows a
separate rule: endpoint fields take priority, while explicit `models` and
offline `model_defaults` fill fields omitted by the endpoint. Explicit models
remain selectable; successful refreshes remove `model_defaults` entries omitted
by the endpoint.

```yaml
model_aliases:
  low_llm:
    model_string: provider/model/low
    usage: Low cost or routine work
    augment_system_prompt: null
  review_llm:
    model_string: provider/model/high
    usage: Careful code review

model_augment_system_prompts:
  provider/model/low: Additional system guidance
```

`model_string` is the canonical target. An empty target is valid: it defines a
disabled, unconfigured alias, which is shown in the model-alias picker and
reported as a startup warning. Selecting it fails clearly until it is
configured. `usage` is required after predefined and user fields are merged.
Alias names are ordinal, case-sensitive identifiers: they must be nonempty,
already trimmed, and contain no `/`. Targets and canonical augmentation keys
must be trimmed `provider/model[/variant]` selectors with no empty or control
whitespace path segment. Aliases cannot chain or refer to themselves; an alias
target must be a canonical selector accepted by a configured provider.

The requested selector remains the session's identity. Thus a session selected
as `high_llm` continues to display and persist `high_llm`, while the provider
executes that alias's resolved canonical target. Injected runtime status and the
`status` tool show this complete requested selector without expanding an alias
to its canonical target. The route is resolved once at the beginning of each
turn. Retargeting an alias affects the next turn only; an active turn, including
its tool rounds, continues to use its captured canonical route and matching
prompt configuration.

A spawned child runs independently and `agent_spawn` returns its session ID
immediately. Friendly child names are unique only among one agent's direct
children. `agent_send` can address the sender's direct parent or a descendant
within the sender's own descendant tree and user session. A descendant uses a
relative, slash-separated friendly-name path such as `child/grandchild`; paths
travel only downward, cannot traverse upward, and do not authorize arbitrary
canonical IDs for descendants. Exact canonical agent session IDs remain accepted
only for the direct parent or direct children, not arbitrary agents elsewhere in
the user session. For a sender with a registered direct parent, the
case-sensitive literal `parent`, actual parent ID, or actual parent friendly name
resolves to that parent and takes precedence over a colliding direct-child
friendly name; direct-child names resolve last. For a root sender, `parent` has
no special meaning and can resolve a direct child with that name.When each child execution finishes, Parrot automatically sends its terminal
status and result to its direct parent as normal steering input.
`agent_spawn.scope` is optional. When omitted or empty, it inherits the
parent's scope. A supplied scope changes only the scope hierarchy in the child
prompt; it is informational only and does not change permissions, session
ownership, or tool access.

The generic `wait` tool pauses for incoming activity and returns early for a new
message, direct-child completion, unclaimed yielded-process completion, or an
item in an accessible queue the invoking agent enabled with `queue_listen`.
Listening state belongs to that invoker, not to the queue, so another consumer
must enable listening independently. A successful wake result remains short.
Timeout output inventories only that agent's accessible queues, alongside its
active processes and direct subagents; `queue_listen` controls wake eligibility,
not inclusion in this agent-facing status. The external client inventory spans
the user session and identifies each queue's owning agent, without changing
which agents can access that queue.
When `exec_command` yields, its result carries the
authoritative typed yielded-process handoff rather than requiring clients to
recognize or parse result text. For a normal non-PTY pipe run, that handoff
provides distinct absolute paths to UTF-8 text stdout and stderr files in the
owning agent's scratch blob area. Newly received Parrot chunks are flushed with
an approximately 100 ms visibility target under normal local load, so the files
can be read while the process runs. Child-process buffering remains outside
Parrot's control. PTY runs are excluded and provide no such paths.

The normal completed-result formatting and overflow notices remain compatible.
All normal-pipe output is retained in the two files after completion and across
session resume; oversized completed output may additionally create the legacy
formatted blob. This deliberately trades disk space for live and retained
output. Quotas and garbage collection are not included.

The service also publishes complete snapshots of the user session's currently
active shell processes from its authoritative in-memory owners. Enhanced clients
replace their local process inventory with each snapshot, including the initial
snapshot after reconnect, so a dropped stream cannot leave stale rows or lose
surviving processes. A process row remains live after the originating tool call
yields and is removed only when a later snapshot reports that the process has
actually exited.

Configured aliases may be used anywhere a model selector is accepted,
including `agent_spawn.model`. An omitted or empty `agent_spawn.model` inherits
the parent turn's complete requested selector, including an alias or variant;
an explicit alias or canonical selector becomes the child's requested selector
and is validated before the child is created. The child resolves its own route
when its turn begins, so later alias changes can affect a later child turn.

`/model-alias` is an interactive, wizard-only command for configuring an
existing alias. It ignores typed arguments, lists aliases by name with their
usage and target (or `not configured`), and lets the user choose a provider,
model, and, where applicable, effort. It configures only the alias; it does not
change the active session or model selection.

Its alias picker also has a **Use provider defaults** entry, described as
**Configure all four model aliases**. Selecting it opens **Select provider
defaults**. That picker lists only provider IDs that both have a complete
four-alias mapping and are currently available through the executing server's
`ListModels` result. Thus a configured provider-default mapping is not offered
merely because it exists in configuration; the server must currently make that
provider available. If none qualify, the command reports `no available
providers have model alias defaults`.

Choosing a provider-default entry is server-authoritative: the server validates
and writes all four ordinary alias targets as one atomic operation, so it never
leaves a partially applied provider set. On success it first reports `Model
aliases configured from PROVIDER defaults:`, then lists every returned mapping
as an indented `NAME = MODEL_STRING` line, ordered ordinally by alias name. A
remote CLI queries and updates that authoritative server configuration; it does
not modify its own local configuration instead.

Aliases can also tailor the system prompt. For a matched alias, a non-null
`augment_system_prompt` wins, including an explicit empty string, which
suppresses augmentation. `null` (or omission) falls through to
`model_augment_system_prompts`: first an exact canonical selector key, then its
canonical base `provider/model` key. An explicit empty alias value is therefore
different from `null`. Direct canonical selectors retain the same exact-then-
base augmentation behavior, and remain compatible with unlisted models that
the provider accepts.

## Configuration

Parrot keeps its configuration under `$XDG_CONFIG_HOME/parrotdotnet` (or
`~/.config/parrotdotnet` when `XDG_CONFIG_HOME` is unset). The shipped
`predefined_config.yaml` is copied there at startup and replaced whenever the
running binary changes. It is the complete, agent-readable reference for the
active defaults; do not edit it. It owns built-in serializable provider
defaults, including offline `model_defaults` catalogues of seed model names
and descriptions. Endpoint metadata takes priority, while `models` and
`model_defaults` fill fields omitted by the endpoint. A successful endpoint
refresh removes seeded entries that are absent from the response. Provider
`models` remain selectable even when the endpoint omits them.

Implementation-specific provider adapters and model-list decoders remain in
code, as does the ChatGPT OAuth transport; the YAML contains only their
serializable defaults and catalog metadata.

OpenRouter provider-routing criteria are configured as an open-ended mapping at
`providers.openrouter.provider_preferences`. The predefined policy enables
fallbacks, requires every selected endpoint to support all request parameters,
denies providers that may collect request data, and requires zero-data-retention
endpoints:

```yaml
providers:
  openrouter:
    provider_preferences:
      allow_fallbacks: true
      require_parameters: true
      data_collection: deny
      zdr: true
```

Fallbacks remain limited to endpoints eligible under the parameter and privacy
filters. A user mapping can override one criterion without repeating the others;
for example, `providers.openrouter.provider_preferences.allow_fallbacks: false`
disables fallbacks while inheriting the three remaining defaults. Weakening
`require_parameters`, `data_collection`, or `zdr` changes the endpoint
eligibility or privacy contract for subsequent OpenRouter requests.

The predefined file also ships the complete model-facing definition for every
built-in tool under `tools`: its description and its standard JSON Schema
`parameters` object. The schema includes property descriptions and all
model-visible types, required fields, defaults, enums, patterns, and bounds.
Runtime tools own execution only; their source-generated JSON deserializers,
explicit validation, sandbox rules, security profiles, and permission checks
remain authoritative.
Put personal settings in `config.yaml` in the same directory. It is never
created or overwritten by loading configuration. Parrot recursively merges its
mapping over `predefined_config.yaml`: nested mappings combine by key, and
scalars and sequences replace their corresponding defaults unless documented
otherwise. Only `sandbox_rules`, `profiles.<id>.sandbox_rules`, and
`cli_utilities.expected` and `.optional` append user entries after predefined
entries; `!replace` restores replacement behavior for one of those sequences.
Thus a `model_aliases.low_llm.model_string` entry can override that target
without repeating its predefined usage. The predefined file contains all seven
profile definitions, so a nested `profiles.<id>` mapping can override one
profile field while inheriting every other field from the active default.

The same recursive rule applies to complete tool schemas. For example, a user
may replace
`tools.question.parameters.properties.questions.items.properties.prompt.description`
or structural members such as `type`, `required`, and bounds while inheriting
all unspecified values. This standard JSON Schema layout intentionally replaces
the former prose-only paths under `tools.<id>.parameters.<field>`; existing
custom overrides must add the `properties` and, for arrays, `items` levels.
Structural overrides can make the model/provider contract diverge from runtime
argument handling. They influence model guidance and provider validation but do
not relax runtime execution or security checks.

Before a tool set becomes active, Parrot audits the merged catalogue against
the complete composed runtime inventory, including tools later removed by
session-support and profile/global filters. Every registered runtime tool must
have exactly one configured definition, and every configured definition must
correspond to a registered runtime tool. An incomplete or stale catalogue fails
closed before a provider call.

### Parallel tool calls

Parallel-safety metadata belongs to the tool implementation and is default-unsafe;
it is not inferred from a tool name or by analyzing shell syntax. In one provider
batch, Parrot starts each maximal consecutive run of calls whose tools declare
the individual invocations safe. An unsafe call is a barrier: it waits for the
preceding safe run, and the next safe run waits for that call. Calls that are
unknown, malformed, or otherwise unsafe are handled by their normal error or
cancellation lifecycle and are not launched as safe work.

Safe calls may finish in any order, but their durable settlement and publication
remain in provider order, and that order is sent in the next provider request.
Existing durable settlements are reused during restoration; only unsettled
calls are reconciled, with the same ordering and barriers.

`exec_command` is the configured exception. It is safe only when, after leading
shell whitespace, its command starts with an entry in
`read_only_exec_command_prefixes` followed by the end of the command or shell
whitespace. The default entries are in `predefined_config.yaml`, and normal
configuration rules let users append or replace them. This is a lexical prefix
check, not general shell analysis; commands that do not match remain unsafe.


## Build And Run

The quickest way to run it, no dev shell needed:

```sh
nix run . -- version
nix run . -- chat "hello"
```

That builds the portable, framework-dependent binary. On Linux, development
and the Native AOT publish happen inside the dev shell; there is no supported
way to build this repository against an ambient SDK.

### macOS source build

macOS does not require Nix. Install the .NET 10 SDK and the Xcode Command Line
Tools, then publish for the architecture of the machine:

```sh
dotnet publish src/Parrot.Cli/Parrot.Cli.csproj -c Release -r osx-arm64
./artifacts/publish/Parrot.Cli/release_osx-arm64/parrot version
```

Use `osx-x64` on an Intel Mac. The macOS publish is Native AOT and uses the
host toolchain supplied by the Xcode Command Line Tools.

### Sandbox and terminal differences

Linux runs shell commands in Bubblewrap and requires `bwrap` plus unprivileged
user namespaces. Its publish output includes the private `parrot-pty-attach`
helper used for pseudo-terminal shell commands.

macOS runs pipe-mode shell commands in the native Seatbelt compatibility
sandbox; no Bubblewrap installation is expected or reported as missing there.
Seatbelt preserves Parrot's filesystem write policy and host network access,
but it does not provide Bubblewrap's Linux namespaces, capability isolation, or
exact process-tree lifecycle guarantees. Treat it as compatibility isolation,
not as a robust boundary for hostile commands. Its policy interface is
deprecated by Apple, so validate protected paths, process signaling, and task
inspection on the target macOS release.

Seatbelt pipe mode does not support Parrot's pseudo-terminal shell mode, so
commands that require an interactive terminal cannot run on macOS. Use
non-interactive commands and ordinary stdin/stdout pipes instead.

## Interactive slash commands

Slash commands are interactive wizards. Enter `/model` to select a provider and
then a model, `/model-alias` to configure a predefined or custom alias, `/mode`
to select a mode, `/clear` to configure a fresh session, or `/auth` to manage
credentials. Most commands ignore text after the command name because the wizard
asks for the complete selection. `/goal <text>` is the exception: it stores a
persistent reminder on the root session using the exact wrapped text
`User set goal is {goal}. Clear exit reminder if end condition met.` (with `{goal}`
replaced by the supplied text), sends one templated steering notice, and shows a
set confirmation. A bare `/goal` clears that reminder without steering and shows
a distinct clear confirmation. Escape or Ctrl-C dismisses an enhanced wizard
without applying partial changes.

The basic CLI prints choices and reads them as lines. The enhanced CLI replaces
only its live input area with a filterable picker, so an active turn's output,
activity, and modeline remain visible. Use Up/Down to navigate, type to filter,
Enter to accept, and Escape or Ctrl-C to dismiss. In the enhanced chat prompt,
type `/` to show slash-command suggestions, type to filter them, use Up/Down to
highlight one, and press Tab to complete it; Enter then runs the completed
command. In the enhanced main chat input, Shift+Enter inserts a newline. Enter
submits after 100ms of quiet; typing or editing during that grace period instead
inserts a newline, allowing fast unbracketed multiline messages. The basic CLI
remains line-based.

The enhanced modeline's `i/s` and `o/s` are live client-side rates. On receipt
of each provider completion, they add that call's exact provider-reported input
and output totals to 30 one-second buckets, then always divide the retained
totals by 30 (including before all buckets have elapsed). Cached input is
already included in input. These samples are transient: they are neither
persisted nor replayed, so a late attachment, reconnect, or session replacement
has no previous rate and sees only later completions. They describe local
completion receipt, not provider-side token-generation timing, and are distinct
from durable session metered usage.

> **`git add` new files before `nix build`/`nix run`.** A flake sees only
> git-tracked files, so an untracked `.cs` file is silently dropped from the
> build — which surfaces as a spurious "type not found" from the sandbox
> compile, not as "you forgot to add a file". `dotnet build` in the dev shell
> does not hit this, because it reads the working tree directly.

```sh
nix develop

dotnet build Parrot.slnx -c Release
dotnet test Parrot.slnx -c Release
dotnet publish src/Parrot.Cli/Parrot.Cli.csproj -c Release -r linux-musl-x64

./artifacts/publish/Parrot.Cli/release_linux-musl-x64/parrot version
```

Linux build and publish output also contains `parrot-pty-attach` beside
`parrot`. The CLI resolves this private pseudo-terminal helper from its own
application directory; Nix additionally installs it in `bin` so the wrapped
CLI and the helper are available from the same package.

The dev shell supplies .NET SDK 10, clang, lld, zlib, and a musl cross
toolchain. Native AOT shells out to a C toolchain and a linker, which on NixOS
are not on a fixed path, so publishing outside the shell fails at the link step.

## Two builds

`nix build` and `dotnet publish` do not produce the same artifact, deliberately.

| | Produces | For |
| --- | --- | --- |
| `nix build` / `nix run` | framework-dependent, needs the .NET runtime, wrapped by Nix | reproducible builds, `nix run`, CI |
| `dotnet publish -c Release` | Native AOT, self-contained, ~19 MB | what ships |

They differ because `buildDotnetModule` publishes framework-dependent, and
native compilation implies `PublishTrimmed` and refuses to have it disabled —
so the Nix package sets `-p:PublishAot=false`. Making `nix build` produce the
AOT binary means teaching the derivation about the clang/lld/musl toolchain the
dev shell already carries, which is not done.

Dependencies are locked in `nix/deps.json`, because the Nix sandbox has no
network. Refresh it whenever a package reference changes:

```sh
nix build .#default.fetch-deps && ./result nix/deps.json
```

## Static musl

The shipped binary is linked statically against musl. It has no interpreter and
no `NEEDED` entry, so there is no dynamic loader or shared-library dependency to
install. It runs without relying on the host system's libc.

```console
$ ldd parrot
        statically linked
```

It costs about 1.4 MB over a glibc-dynamic build (5.8 MB against 4.4 MB), and
release symbols are retained so a core dump from a shipped binary remains
usable.

One wrinkle is wired into the flake rather than left as folklore. ILCompiler
treats a musl RID on a glibc host as a cross build and passes clang's
`--target=`; the nixpkgs musl toolchain is gcc, already targets musl, and
rejects that flag. The shell therefore provides `musl-clang`, a shim that drops
`--target=` and forwards the rest. `Parrot.Cli.csproj` selects it automatically
whenever the RID is `linux-musl-x64`, so there are no flags to remember beyond
`-r`.

## Gates

A change is ready when all of these pass:

```sh
dotnet build Parrot.slnx -c Release          # warnings are errors
dotnet test Parrot.slnx -c Release
dotnet format Parrot.slnx --verify-no-changes
dotnet publish src/Parrot.Cli/Parrot.Cli.csproj -c Release -r linux-musl-x64
nix flake check
```

There is no "fix the warning later" state: `TreatWarningsAsErrors` is on for
every project, `AnalysisLevel` is `latest-all`, and IDE code-style rules run as
part of the build. The analyzer configuration is recorded in
[`.editorconfig`](.editorconfig).

## For Contributors

Read [AGENTS.md](AGENTS.md) before changing code. It documents the development
guidelines, supported environment, required gates, and repository conventions.
