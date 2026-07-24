# Top-Level Architecture

**Status: draft, first pass. Not yet reviewed.** This is the level-1 deliverable
of the plan gate (MIGRATION.md §0). It names the top-level classes and how they
connect. It does not yet decompose any of them, and no component may be ported
until its entry in [components.md](components.md) is filled in from this.

Derived by reading the Go tree, not invented: the block set below is
`app.App`'s field list plus what `httpapi.DomainBackend` carries, with the
HTTP/SSE contract re-specified as gRPC. Where the Go
name is a package-level `Service`/`Registry`/`Manager`, the C# name says what it
manages, because in C# the namespace no longer disambiguates it.

## The blocks

```text
+---------------------------------------------------------------------------+
| CLIENTS   Program             CommandDispatcher                           |
|           BasicCli            EnhancedCli                                 |
+---------------------------------------------------------------------------+
| TRANSPORT GrpcServer          ParrotService          InProcessChannel     |
+---------------------------------------------------------------------------+
| ROOT      ParrotApplication                                               |
+---------------------------------------------------------------------------+
| DOMAIN    UserSession         AgentSession           AgentRegistry        |
|           TaskManager         Compactor              SystemContextBuilder |
+---------------------------------------------------------------------------+
| TOOLS     ToolRegistry        ITool                  PermissionBroker     |
|           QuestionBroker      ProcessRunner          WebFetcher           |
+---------------------------------------------------------------------------+
| PROVIDERS ProviderRegistry    ILLMProvider           ICredentialStore     |
+---------------------------------------------------------------------------+
| STORAGE   SessionDatabase     EventBroker            EventRepository      |
|           Configuration       StatePaths                                  |
+---------------------------------------------------------------------------+
```

## The request path

Everything is a gRPC server; the CLIs sit entirely behind it and are pure
clients. Both speak the same service and consume the same flat event stream,
which is principle 11 — the local CLI and a remote client use one contract.

```text
   +----------------+            +----------------+
   |    BasicCli    |            |  EnhancedCli   |   two clients, no
   |  print a line  |            |  full TUI      |   shared code between
   +--------+-------+            +--------+-------+   them (see below)
            |                             |
            +--------------+--------------+
                           |
                   generated gRPC stub
                           |
              +------------+------------+
              |                         |
     +--------v---------+      +--------v--------+
     | InProcessChannel |      |   GrpcServer    |   unix socket or port
     |  local mode,     |      |   remote mode   |
     |  no socket       |      |                 |
     +--------+---------+      +--------+--------+
              |                         |
              +------------+------------+
                           |
                           v
                  +-----------------+
                  |  ParrotService  |   the gRPC contract: commands in,
                  +--------+--------+   one flat event stream out
                           |
                           v
                   ( the domain, below )
```

### What gRPC costs

Measured on this toolchain, not estimated — a minimal ASP.NET Core + gRPC
service, published Native AOT for linux-x64:

```text
                        with symbols   stripped
current parrot binary        2.7 MB      --
gRPC + ASP.NET Core           32 MB      11 MB
```

That is roughly a 4x floor before any Parrot code exists, and it is the one
place this design fights the premise of a small single binary. It also collides
with keeping symbols, which the build does deliberately to match the Go build's
`dontStrip` — 32 MB is a large download to hand someone.

Worth knowing before committing: the cost is Kestrel and the ASP.NET Core
hosting stack, not the protobuf codec. A hand-rolled framed protocol over a unix
socket would keep the binary near its current size and would still give the CLIs
a flat event stream. gRPC buys a schema, generated clients in any language, and
streaming that already works. That is likely worth 11 MB, but it should be a
decision rather than a discovery.

## The dependency tree

Who calls whom. Read the indentation as "depends on".

```text
ParrotService
 |
 +-- UserSession ................. one per working directory. owns the database
 |    |                           and holds the claim on it for its lifetime
 |    +-- SessionDatabase ........ one SQLite file, exactly one writing machine
 |    +-- Configuration ........ the shared exception; see below
 |    |
 |    +-- AgentSession ........... a rich object, not a record plus a service.
 |    |    |                      it owns its state, its drain, and its turns
 |    |    |                                        [principles 2, 3, 4, 6]
 |    |    +-- SessionDatabase ... loads and persists it
 |    |    +-- EventRepository
 |    |    +-- SystemContextBuilder .. sampled only at a safe turn boundary
 |    |    +-- Compactor
 |    |    +-- AgentRegistry .... agent profiles; also spawns and owns
 |    |    |    |                 child sessions
 |    |    |    +-- AgentSession  (recurses: a child session, same database)
 |    |    |
 |    |    +-- ToolRegistry ..... immutable snapshot per turn
 |    |    |    +-- ITool  <<extension boundary>>
 |    |    |         +-- ProcessRunner .. sandboxed exec, fails closed
 |    |    |         +-- WebFetcher
 |    |    |
 |    |    +-- ProviderRegistry
 |    |    |    +-- ILLMProvider  <<extension boundary>>   stateless
 |    |    |         +-- ICredentialStore  <<extension boundary>>
 |    |    |
 |    |    +-- TaskManager ...... the task tree
 |
 +-- TaskManager
 +-- PermissionBroker ............ authorises an operation, not a tool name
 +-- QuestionBroker
 +-- EventBroker ................. serialised publication    [principle 9]
      +-- EventRepository
           +-- SessionDatabase

ParrotApplication                  the composition root: constructs and owns
 |                                 every singleton above, explicitly, by hand
 +-- Configuration ............... config and centralized state, shared
 +-- StatePaths
      +-- SessionDatabase ........ one per user session, never two hosts
```

## The Run tree

Whose lifetime bounds whose. This is a different graph from the one above, and
`AGENTS.md` makes it the load-bearing one: cancellation flows down, nothing
flows up, and a parent `Run` does not return while a child `Run` is in flight.

A block absent here is passive — it is called, it does not run.

```text
CommandDispatcher.Run                       returns => the process exits
 |
 +-- GrpcServer.Run ....................... serve mode only
 +-- BasicCli.Run ......................... local mode, one of the two
 +-- EnhancedCli.Run ...................... local mode, the other
 +-- UserSession.Run ...................... the cwd claim is held for exactly
 |    |                                     this Run, and released when it
 |    |                                     returns
 |    +-- AgentRegistry.Run ............... hosts spawned child sessions
 |    |    |                                a child outlives the turn that
 |    |    |                                spawned it, so it is not nested
 |    |    +-- AgentSession.Run ........... one child session
 |    |
 |    +-- AgentSession.Run ................ one drain per agent session
 |         |                                a turn is a loop iteration here,
 |         |                                not a nested Run
 |         +-- ProcessRunner.Run .......... one child process
```

There is no `Stop` anywhere. Shutdown is cancellation of the token `Program`
holds; `IDisposable` releases handles after `Run` has already returned.

## The two CLIs

There are two clients and they share no code. Not "share little" — none.

`BasicCli` is the fallback and the reference. It reads the flat event stream and
prints lines. No alternate screen, no cursor addressing, no widgets, no model of
the conversation beyond what it has already printed. It should be small enough
that a person could rewrite it from the `.proto` alone in an afternoon.

`EnhancedCli` is the full terminal experience — scrollback-preserving chat,
editor, picker, markdown rendering.

### Why no shared code

Upstream has `cli/chat` (3056 lines), `cli/enhancedchat` (3572 lines), and
`cli/chatview` (1937 lines) which both import. That shared view layer is the
thing being designed out, for two reasons.

The first is that a 3056-line "basic" CLI is not basic. Whatever it is for —
a dumb terminal, a CI log, a pipe, a bug report where the TUI is the suspect —
it only serves that purpose if it is obviously correct at a glance.

The second matters more. **`BasicCli` is the test of the event contract.**
Events are flat and carry their own `task_id` and `session_id` precisely so a
client needs no tree, no correlation table, and no subscription per subagent.
If `BasicCli` cannot render an event with a `switch` and a `WriteLine`, the
event is underspecified — and the shared view layer is exactly what hides that,
because both clients inherit the same compensating logic and neither one ever
proves the events were sufficient.

So the rule is a design constraint, not tidiness:

> If `BasicCli` needs a helper, fix the event, not the CLI.

### Enforcing it

Nothing but a machine check keeps two clients apart once one of them grows an
attractive helper. The intended enforcement is a `PARROT0004` analyzer rule:
nothing under the basic client's namespace may reference the enhanced one, or
any third namespace shared only by the two of them. Both may reference the
generated gRPC stub and the proto message types, since those are generated from
the contract rather than written.

Not implemented yet — the namespaces do not exist.

## UserSession

A `UserSession` is one working directory's session. Starting `parrot` in a
directory that has no live session starts one; starting it where a session is
already live starts a second rather than joining it. Each owns its own database.

This is not a convenience. It is the only structure that survives a home
directory on NFS.

### Why the databases are separate

A shared filesystem cannot be assumed to provide working locks. A mount may
grant every advisory lock locally and tell no other host, so two machines both
believe they hold an exclusive lock and both write. SQLite corrupts silently
under that, and so would any embedded database, because they all rest on the
same primitive.

So the division is **structural, not lock-based**: a working directory is a
host-local name, so keying a database by working directory guarantees that one
machine writes it. Nothing negotiates. Nothing needs to.

```text
<state>/config.yaml ................. shared. every host reads and writes it
<state>/sessions/<id>/session.db .... one user session, one writing machine
<state>/sessions/<id>/meta.json ..... published by rename; the listing reads
                                      this, never another host's database
<state>/owners/<hash>/v<N>.json ..... per host. claimed with link(), which
                                      reports EEXIST instead of overwriting
```

Four rules follow, and all four are load-bearing:

1. **One machine writes one session database.** It holds every table belonging
   to that user session, so its foreign keys stay inside one file. It uses
   `journal_mode=TRUNCATE`, because WAL coordinates through a memory-mapped
   `-shm` file and two hosts mapping one file get incoherent private views
   rather than shared state. No `-shm` or `-wal` may ever appear under the
   state directory.
2. **Listing reads `meta.json`, never another host's database.** Entries are
   published by rename, which a reader cannot observe half-written. The
   database stays the source of truth; the entry is a projection.
3. **Owner records are per host, and a claim uses `link()`.** `rename` would
   silently discard a competing claim; `link` onto a version-named target
   reports `EEXIST` instead. No lock manager is involved. Startup atomically
   reclaims a binding abandoned by a dead process; a live binding causes a
   second user session instead.
4. **Repair never ranges across user sessions.** A process cannot tell whether
   work in another machine's session is abandoned or in flight.

### The configuration exception

`config.yaml` is the one file every host reads and writes. It is both the
configuration and the centralized atomic state — the place for things that are
genuinely global, like flags, which would be meaningless if each working
directory held its own copy.

It gets away with being shared because it is small, whole-file, and written
atomically by rename. There is no partial write for a reader to observe and no
range for two writers to interleave within. That is the whole reason the
exception is safe, and it is also the constraint on what may go in it: anything
that needs a read-modify-write against concurrent writers on another host does
not belong here, because rename gives atomicity, not serialisation.

`Configuration` is therefore **not immutable at runtime**, unlike the merged
view each user session resolves from it at startup.

## AgentSession

`AgentSession` is the one block that must not be a record plus a service.
Upstream `session.Session` is a twelve-field struct with **no methods**, and its
behaviour lives in five other places — `Service`, `GoalService`, `TodoService`,
`agentSession`/`drainState` in the coordinator, and the turn runner. That is the
anemic model `AGENTS.md` rejects, and MIGRATION.md §5 names it by that shape.

It carries a lot of state, which is why it is the block most likely to be got
wrong. Everything below belongs to one session and nothing outside it reads
any of it:

```text
AgentSession
 |
 +-- identity ......... Id, ParentSessionId, ProjectId, ProjectRoot, Title,
 |                      CreatedAt, UpdatedAt
 +-- selection ........ Agent, Provider, Model, Variant, and patches to them
 +-- drain ............ Idle | Running | Interrupting, and who owns the drain
 +-- owner ............ WorkingDirectory, HostKey, Pid  (interactive binding)
 +-- input ............ admitted prompts not yet promoted, each steer or queue
 +-- messages ......... the projected conversation, assistant finals, tool calls
 +-- epoch ............ the context epoch: baseline text, typed source
 |                      snapshots, history cutoff        [principle 4]
 +-- todos ............ upstream TodoService
 +-- goals ............ upstream GoalService
 +-- mainTask ......... root of this session's task tree
```

### Drain states

At most one foreground drain owns a session in one process (principle 2). The
drain is process-local coordination, not a durable entity — recovery rebuilds
from admitted prompts, projected messages, epochs, and terminal tool states.

```text
                 promote input
      +---------------------------------+
      |                                 |
      v          interrupt              |
  +--------+   requested   +--------------+
  |  Idle  |               | Interrupting |
  +--------+               +--------------+
      ^                         ^     |
      |                         |     | turn cancelled,
      | turn settled,           |     | tools settled
      | nothing to promote      |     v
      |                    +---------+
      +--------------------| Running |
                           +---------+
```

`Interrupting` exists because a turn is a cancellable boundary that must still
settle: every local tool call finishes before the next turn (principle 6), so
cancellation cannot simply drop the drain.

### A turn

A turn is a loop iteration inside `AgentSession.Run`, not a separate object and
not a nested `Run`. `AgentSession` performs this sequence per turn, in order. It
is the safe provider-turn boundary, and context sources are sampled nowhere
else:

```text
1. initialize or reconcile the context epoch
2. promote eligible input          steer: at this boundary
                                   queue: when the turn would otherwise stop
3. resolve the agent and model
4. load active history
5. materialize an immutable tool registry snapshot
6. compact history if required
7. call ILLMProvider                          <-- the only stateless step
8. execute the returned tool requests, all settling before step 1 repeats
```

## ILLMProvider

Stateless, by rule. It holds no conversation, no session, no accumulated
history — `AgentSession` holds all of it and passes what a call needs. Two
providers serving the same session concurrently would be a bug in the session,
not a race in the provider.

The surface is deliberately shallow: a prompt goes in, messages and tool
requests come out. Everything upstream models as protocol events, retry
notices, or stream lifecycle stays inside the implementation.

```csharp
public interface ILLMProvider
{
    string Id { get; }

    IReadOnlyList<LLMModel> Models { get; }

    // Everything the call depends on arrives in the request. Nothing is
    // remembered between calls.
    Task<LLMResponse> Prompt(LLMRequest request, CancellationToken cancellationToken);
}
```

`LLMRequest` carries the model and variant, the system context baseline, the
message history, and the tool schemas available this turn. `LLMResponse` carries
the assistant messages and the tool requests, plus token usage.

Streaming is the open part of this shape: upstream returns a `Stream` the caller
pumps, and live token deltas are disposable while final message state is durable
(principle 10). A single `Task<LLMResponse>` cannot express a delta. The likely
resolution is that `Prompt` returns the final response while deltas are
published to the event stream as a side effect, which keeps the interface flat
and matches principle 10 — but it is not decided. See open question 1.

## AgentRegistry

It resolves agent profiles and it spawns child sessions, because upstream's
subagent manager was already asking the registry its questions —
`agentIdentity` and `agentRecursionLimit` are its own methods. Folding removes
that reach-across.

What it owns beyond profiles: the child task table, per-parent concurrency
limits, recursion limits, and the lifetime of every spawned child session.

Two consequences, neither cosmetic:

- **It is no longer passive**, so it appears in the Run tree. A spawned child
  outlives the turn that spawned it — upstream `Spawn` returns an id and the
  caller `Await`s later — so a child session is *not* nested under the parent's
  `Run`. The previous draft nested it, which was wrong.
- **It and `AgentSession` now depend on each other.** `AgentSession` asks it to
  resolve an agent; it constructs and runs `AgentSession`. Both rank 9. That is
  inherent to subagent recursion rather than a modelling error, but it means
  neither can be built without at least a stub of the other.

The name is now doing less work than it should: a type that owns lifetimes is
not a registry. `AgentRuntime` or `Agents` would be more honest. Not renamed,
because the fold was the instruction and the name was not.

## Blocks to Go packages

Rank is migration order. A block may not be built before anything it depends on.

| Rank | C# class | Absorbs (`internal/…`) |
| --- | --- | --- |
| 1 | `StatePaths` | `appdirs`, `project`, `id`, `atomicfile`, `processidentity` |
| 1 | `Configuration` | `config`, `mode` |
| 2 | `SessionDatabase` | `store` (database, meta), `workspace` |
| 2 | `EventRepository` | `event` (persistence half) |
| 3 | `EventBroker` | `event` (broker, stream, subscription) |
| 3 | `ICredentialStore` | `auth`, `security` |
| 4 | `TaskManager` | `task`, `status`, `monitor` |
| 5 | `ILLMProvider`, `ProviderRegistry` | `provider`, `protocol` |
| 5 | `PermissionBroker`, `QuestionBroker` | `permission`, `question` |
| 6 | `ProcessRunner` | `process` |
| 6 | `WebFetcher` | `webfetch` |
| 7 | `ITool`, `ToolRegistry` | `tool`, `change` (patch model and parsing only) |
| 7 | `SystemContextBuilder` | `systemcontext`, `skill`, `command` |
| 8 | `Compactor` | `compaction` |
| 9 | `AgentSession` | `session` (conversation half), `agent` (runner and coordinator) |
| 9 | `AgentRegistry` | `agent` (registry, provider resolution), `subagent` |
| 10 | `UserSession` | `session` (InteractiveOwner, InteractiveClaim), `store` (owners, claims) |
| 11 | `ParrotService` | `api/v1`, `httpapi` (backend half), re-specified as a `.proto` |
| 11 | `InProcessChannel` | `transport`, `client` |
| 12 | `GrpcServer` | `httpapi` (server, routes) |
| 12 | `ParrotApplication` | `app` |
| 13 | `CommandDispatcher` | `cli/cli.go`, `diagnostics` |
| 13 | `BasicCli` | `cli/chat`, rewritten far smaller. **Not** `cli/chatview` |
| 14 | `EnhancedCli` | `cli/enhancedchat`, `cli/chatview`, `terminal` |

Two blocks rank far later than their state alone would suggest, both for the
same reason: they own a lifetime, and a lifetime depends on everything it runs.
`AgentSession` is rank 9 because it owns the drain and the turn, and a turn
needs the tool registry and the providers. `UserSession` is rank 10 because it
owns the agent sessions inside it. This is why `SessionDatabase` is ranked 2 —
the state becomes persistable long before either owner can be built.

## Open questions

Resolve these before filling in `components.md`; each one moves a boundary.

1. **How `ILLMProvider` streams.** A flat `Task<LLMResponse>` cannot express a
   token delta, and principle 10 wants deltas disposable but final message state
   durable. Publishing deltas to the event stream as a side effect keeps the
   interface shallow, which is the point of the shape, but it makes the provider
   write somewhere — and the rule says it is stateless. Whether "stateless"
   means "holds no conversation state" (it can publish) or "has no outbound
   dependency at all" (`AgentSession` pumps and publishes) is the decision.
2. **Does `AgentSession` sub-divide?** It owns ten groups of state, plus the
   drain and the turn loop. That is a lot for one type even when the type is
   correctly rich. Todos and goals are the obvious candidates for owned
   sub-objects — `AgentSession.Todos` rather than a `TodoService` — but that is
   a decomposition question for level 2, not a reason to hand them back to a
   service.
3. **`EventBroker` and `EventRepository` are drawn apart but commit together.**
   Principle 9 requires the durable event and its projection to commit
   atomically. If that forces one transaction, they are one block, not two.
4. **`ToolRegistry` snapshot immutability.** Principle 4 wants an immutable
   registry snapshot per turn. Whether that is a type or a discipline decides
   if `ToolRegistry` is a block at all.
5. **`ICredentialStore` versus provider auth.** ChatGPT OAuth refresh is a
   provider concern that writes to the credential store. Which side owns the
   refresh decides whether the dependency arrow reverses.
6. **Is gRPC worth 11 MB?** Measured above: ASP.NET Core hosting, not the
   codec, is what costs. A framed protocol over a unix socket keeps the binary
   near its current size and still gives the CLIs a flat event stream. gRPC
   buys a schema, generated clients, and working streaming. Decide it rather
   than discover it.
7. **`EnhancedCli` is 3.5k lines of `enhancedchat` plus 4.6k of `terminal`.**
   Almost certainly several trees. Ranked last so the shape can be decided once
   everything it renders exists. `BasicCli` has the opposite problem: it is
   ranked early precisely so the event contract gets tested before the TUI can
   paper over a gap in it.
