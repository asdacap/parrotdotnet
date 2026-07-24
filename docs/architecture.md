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
| TOOLS     IToolFactory        ITool                  PermissionBroker     |
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

Measured, not estimated. All published Native AOT, static musl, stripped — the
configuration this project actually ships. Each row adds one thing to the row
above it:

```text
                                        size     delta
console app, nothing in it              1.2 MB     --     the floor
+ ASP.NET Core hosting, DI, routing     6.3 MB   +5.1 MB  <-- the real cost
+ gRPC                                  8.1 MB   +1.8 MB
+ Kestrel                              10.0 MB   +1.9 MB

protobuf + unix socket framing          3.0 MB   +1.8 MB  no ASP.NET Core
```

Three things fall out, and each one kills an option that looks promising.

**gRPC is not what costs.** The service model, the codec, and the generated code
come to about 1.8 MB — roughly what a bare protobuf codec costs anyway.

**Neither is Kestrel, quite.** ASP.NET Core's `IServer` really is pluggable, and
gRPC links and publishes with Kestrel swapped for a stub server. It saves
1.9 MB. But replacing it for real means implementing HTTP/2 — framing, HPACK,
flow control — because that is what gRPC requires on the wire. That is strictly
more work than the framing option below, for a fraction of the saving. Swapping
the server is the worst of both: most of the size, all of the work.

**The cost is the hosting stack**: +5.1 MB for DI, the middleware pipeline, and
routing, before any server or any gRPC exists. It cannot be removed while using
grpc-dotnet's server, because that server *is* an ASP.NET Core framework —
[grpc-dotnet](https://github.com/grpc/grpc-dotnet) describes `Grpc.AspNetCore`
as "an ASP.NET Core framework for hosting gRPC services", and ships no server
that hosts any other way. Its client half is cheap; it is `HttpClient` over
HTTP/2, so neither CLI is what costs. The only other implementation,
`Grpc.Core`, is the C-core binding: in maintenance mode since May 2021 and
slated for deprecation, and a native shared library, so it could not be
statically linked here regardless.

So there is no middle option to go looking for. The choice is 10 MB for a
standards-compliant HTTP/2 server, or 3 MB for length-delimited protobuf over a
unix socket — which keeps the `.proto` as the schema and `Grpc.Tools` for
message codegen, and loses the parts that come from HTTP/2: standard clients
working out of the box, `grpcurl`, and streaming semantics that already handle
half-close, flow control, and deadlines. Those last ones are where the bugs
live, and hand-rolling them is how a 100-line framing layer becomes 800.

The framing option suits this design unusually well, though. Local mode uses
`InProcessChannel` and opens no socket at all, so the HTTP/2 server exists only
for the remote case. And `BasicCli` would need no gRPC dependency whatsoever —
just the generated message types and a read loop.

### Decided: keep gRPC

Roughly 10 MB, accepted. The 7 MB buys streaming that already handles
half-close, flow control, deadlines, and cancellation — the part that is easy to
start and hard to finish — plus a schema that generates clients in any language
and works with existing tooling. Hand-written framing would put that lifecycle
in the critical path of every session, which is the last place to want novel
code.

Two consequences follow, and both are now settled rather than open:

- **The `.proto` declares services and streams**, not messages only.
- **`BasicCli` depends on the generated gRPC client.** That is the one thing it
  shares with `EnhancedCli`, and it stays allowed under the two-CLI rule for the
  reason the rule already gives: generated code is derived from the contract
  rather than written, so it cannot hide a gap in the event model the way a
  hand-written view layer would.

The size floor is worth revisiting only if the binary becomes a real complaint.
The measurements above are the starting point if so; nothing else in the stack
is left to try.

## The dependency tree

Who calls whom. Read the indentation as "depends on".

```text
ParrotService
 |
 +-- UserSession ................. one per working directory. owns the database
 |    |                           and holds the claim on it for its lifetime
 |    +-- SessionDatabase ........ one SQLite file, exactly one writing machine
 |    +-- Configuration ........ the shared exception; see below
 |    +-- shell processes ....... named runs shared by main and child agents;
 |    |                           cancelled and joined with the user session
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
 |    |    +-- IToolFactory ..... one per tool, per user session
 |    |    |    +-- ITool  <<extension boundary>>   one per agent session
 |    |    |         +-- ProcessRunner .. sandboxed exec, fails closed
 |    |    |         +-- WebFetcher
 |    |    |
 |    |    +-- ProviderRegistry
 |    |    |    +-- ILLMProvider  <<extension boundary>>   stateless
 |    |    |         +-- ICredentialStore  <<extension boundary>>
 |    |    |
 |    |    +-- TaskManager ...... this session's tasks; they do not nest
 |
 +-- TaskManager
 +-- PermissionBroker ............ authorises an operation, not a tool name
 +-- QuestionBroker
 +-- EventBroker ................. serialised publication    [principle 9]
 |    +-- Event ................. the flat wire event
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
 |                                          a turn is a loop iteration here,
 |                                          not a nested Run
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
Events are flat and carry their own `session_id` and `task_id` precisely so a
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
 +-- tasks ............ the tasks this session started; a flat set
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
history — `AgentSession` holds all of it and passes what a call needs.

```csharp
internal interface ILLMProvider
{
    string Id { get; }

    Task<IReadOnlyList<LLMModel>> ListModels(CancellationToken cancellationToken);

    IAsyncEnumerable<LLMEvent> Call(LLMRequest request, CancellationToken cancellationToken);
}
```

An earlier draft took an `ILLMEventSink` and returned `Task<LLMResult>` — push
rather than pull — and argued that a sink kept "stream deltas *and* return a
final result" from being awkward. That was solving a problem the design did not
have to have.

**The last event is the result.** `LLMEvent.Completed` carries the finish reason
and the token counts, so a consumer that reads to the end has the outcome and
there is no second return channel. Principle 10 still holds and is easier to
see: everything before the last event is disposable, the last one is not.

Three things fall out of dropping the sink:

- **A type disappears.** `ILLMEventSink` existed only to be passed to one
  method, and `LLMResult` and `LLMUsage` with it.
- **`AgentSession` stops implementing an interface it had no business
  implementing.** It was `ILLMEventSink` purely so it could be handed to a
  provider, which put a `Publish(LLMEvent, …)` method on the session's surface
  that was really an artefact of how one call worked. Now it just consumes with
  `await foreach`.
- **It matches the rule already written down.** MIGRATION.md §3 says streaming
  returns `IAsyncEnumerable<T>` with `[EnumeratorCancellation]`. The sink was
  inconsistent with our own guidance, which is a decent sign it was wrong.

The provider is still trivial to test: iterate it and collect.

## The two event types

There are two, and conflating them is the mistake worth naming up front.

`LLMEvent` is what a provider emits. It is internal, never leaves the process,
and carries **no identity** — a stateless provider was never told a session id
or a task id, so it cannot attach one.

`Event` is what a client consumes. It is the generated protobuf type, it goes
down the wire, and it carries identity because `AgentSession` attaches it.

```text
ILLMProvider --LLMEvent--> AgentSession --Event--> EventBroker --> the CLIs
                                        ^
                                 attaches session_id
                                 and task_id here
```

That middle step is a translation layer, which is normally a smell — it is where
a gap in the event model gets quietly filled in. This one is legitimate for a
specific reason: it **adds** the identity the provider structurally could not
know, and does not reinterpret content. If it ever starts reshaping meaning
rather than labelling it, the event model is wrong.

### ILLMEventSink

```csharp
public interface ILLMEventSink
{
    ValueTask Publish(LLMEvent llmEvent, CancellationToken cancellationToken);
}
```

### LLMEvent

Four kinds, and only four, because the stream carries what is worth *watching*
while a call is in flight. Anything worth *keeping* is in `LLMResult` — token
usage and the final tool requests live there, not here, which is principle 10
deciding the split rather than taste.

```csharp
public enum LLMEventKind
{
    TextDelta,
    ReasoningDelta,
    ToolCallDelta,
    Retry,
}
```

```csharp
public sealed record LLMEvent
{
    public required LLMEventKind Kind { get; init; }

    // TextDelta and ReasoningDelta: the fragment.
    // ToolCallDelta: the arguments fragment. Retry: why.
    public string Text { get; init; } = string.Empty;

    // ToolCallDelta only.
    public string ToolCallId { get; init; } = string.Empty;

    public string ToolName { get; init; } = string.Empty;

    // Retry only.
    public int Attempt { get; init; }

    public TimeSpan RetryAfter { get; init; }

    public static LLMEvent TextDelta(string fragment) =>
        new() { Kind = LLMEventKind.TextDelta, Text = fragment };

    public static LLMEvent ReasoningDelta(string fragment) =>
        new() { Kind = LLMEventKind.ReasoningDelta, Text = fragment };

    public static LLMEvent ToolCallDelta(string toolCallId, string toolName, string argumentsFragment) =>
        new()
        {
            Kind = LLMEventKind.ToolCallDelta,
            ToolCallId = toolCallId,
            ToolName = toolName,
            Text = argumentsFragment,
        };

    public static LLMEvent Retry(int attempt, TimeSpan retryAfter, string reason) =>
        new() { Kind = LLMEventKind.Retry, Attempt = attempt, RetryAfter = retryAfter, Text = reason };
}
```

Three things about that shape are forced rather than chosen.

**It is one flat type with a discriminator, not a hierarchy.** The natural C#
spelling would be an abstract record with a case per kind. `AGENTS.md` forbids
inheritance outright — *"or just straight up no inheritance, just compose"* —
and C# has no discriminated union, so flat-plus-`Kind` is what is left. It also
happens to be the shape the wire `Event` already has, which is mild evidence it
is not a compromise.

**No field is nullable.** Absent means `string.Empty` or zero, never `null`.
That is not incidental: `PARROT0003` bans the null-forgiving operator, so a
nullable union field would force a real check at every read site, on a value the
`Kind` already determines. Empty defaults sidestep the whole argument.

**Construction goes through the factories.** The invalid states — a `Retry`
carrying a `ToolName`, a `TextDelta` with an attempt count — are unreachable
without going out of your way, and the reader of a call site sees the kind in
the method name rather than inferring it from which properties were set.

Consumers `switch` on `Kind`. `AgentSession` maps each to an `EventKind`,
attaches `session_id` and `task_id`, and renders the one-line `text` the wire
event requires.

This listing was compiled against the repository's analyzers before being
written down. Empty defaults are `string.Empty`, not `""` (SA1122), and the
properties carry blank lines between them (SA1516). The sink parameter is
`llmEvent` rather than `@event` because escaping a keyword in your own parameter
name is a smell, not because a rule demands it — CA1716, which flags keyword
clashes for cross-language implementers, is off repo-wide since Parrot is an
application and not a library.

### The session event

```proto
message Event {
  string id         = 1;
  string session_id = 2;

  oneof payload {
    TurnStarted turn_started = 3;
    TextChunk   text_chunk   = 4;
    // ... one per thing that can happen
  }
}
```

**The payload is the only discriminator.** An earlier draft of this document
carried a parallel `EventKind` enum and a rendered `text` line on every event,
and argued that the line was load-bearing — that it was what made `BasicCli` a
`switch` and a `WriteLine`, and what turned "fix the event, not the CLI" into
something checkable. Both were dropped, and the reasoning was wrong in two
separate ways.

The enum was **a second source of truth**. Protobuf already discriminates a
`oneof`, every reader already switches on it to reach the fields it wants, and
nothing prevents a `kind` from disagreeing with the payload beside it. Two
discriminators is one too many.

The `text` field was **a second rendering**. It made the server decide how a
client displays something, which is not the server's business; no client is
obliged to use it; and it drifts from the payload it summarises the moment
either changes. A payload that carries what it *means* lets each client render
what it *wants* — which is the actual guarantee, and a stronger one.

What survives is the real constraint: **`BasicCli` switches on the payload and
prints, with no conversation model and no helper.** If it ever needs one, the
payload is underspecified. That test never depended on there being a `text`
field; it only ever depended on payloads carrying enough.

There is also no `task_id`. Nothing starts a task until M5, so the field would
carry a fabricated id no client could use and no test could exercise.

### Why one method and not several

`Publish(...)` rather than `OnTextDelta`, `OnToolCall`, `OnRetry`. A typed
method per event kind means every new kind changes the interface and every
implementation, which is Open–Closed failing — the one SOLID principle
`AGENTS.md` weights as *yes*. Interface Segregation would argue the other way
and is weighted *eh*, which is exactly the trade being made.

It is an interface rather than a .NET `event` or an `Action<T>` because
`PARROT0001` and `PARROT0002` forbid both, and for the reason those rules exist:
a multicast delegate has no delivery ordering across subscribers and no
backpressure, and principle 9 needs both.

It is push rather than `IAsyncEnumerable<T>` because the provider is producing
while it is also working. A pull model would force the provider to be an
iterator and would make "stream deltas *and* return a final result" awkward in a
way the sink makes trivial.

`ValueTask` because the common implementation writes to a `Channel<T>` and
completes synchronously. Backpressure, when it matters, is the channel's bound.

### Durability

`LLMEvent`s are live deltas, which principle 10 makes disposable — so cancelling
a `Publish` mid-flight loses nothing that matters. The durable write happens when
`AgentSession` commits the `LLMResult`, through `EventRepository`, atomically
with its projection (principle 9).

## Tasks and sessions

A task belongs to exactly one session: **the session that started it is its
parent.** Sessions nest — a subagent runs in a child session with a
`parent_session_id` — but tasks do not. There is no task tree and no
`parent_task_id`, and a session does not have a task id of its own.

This diverges from upstream, which parents tasks to other tasks and roots the
tree at a session's main task. Two things get simpler:

- **A client needs no correlation table.** An event names its session and its
  task, and that is the whole story. Reconstructing a task tree from
  `parent_task_id` was work every client had to repeat, and it is exactly the
  kind of work `BasicCli` is not allowed to do.
- **Recursion has one shape, not two.** Nesting happens at the session level
  only, which is already where the child-outlives-the-turn semantics and the
  recursion limits live.

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
| 7 | `ITool`, `IToolFactory` | `tool`, `change` (patch model and parsing only) |
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
needs the tools and the providers. `UserSession` is rank 10 because it owns the
agent sessions inside it. This is why `SessionDatabase` is ranked 2 — the state
becomes persistable long before either owner can be built.

**One deliberate inversion, granted rather than implicit.** A tool instance
belongs to one `AgentSession` and is constructed with it, and its `IToolFactory`
belongs to one `UserSession` and may be constructed with that. So rank 7 names
rank 9 and rank 10, against the direction of every other row here. What that
buys is a tool that can hold state and see its session, which no context object
passed per call can give it; what it costs is that `ITool` is no longer
placeable without the session types. `ISubagentHost` existed to avoid exactly
this and is gone — `AgentSpawnTool` now holds both sessions directly, which is
also what lets it register a spawned child on the user session while it runs.

## Decisions

The level-1 questions, answered. Each one moved a boundary, which is why they
were settled before `components.md` was written rather than after.

### 1. `AgentSession` owns todos and goals as sub-objects

Not separate blocks, not services. `AgentSession.Todos` and
`AgentSession.Goals` are types that own their own state and behaviour, held by
the session and reachable only through it. Nothing outside the session reads
either, which by the garden test makes them branches rather than trees.

The alternative — hoisting them to blocks — would have needed a way to find the
todos for a session, and that lookup is the first step back toward the anemic
shape this design exists to avoid.

### 2. `EventBroker` and `EventRepository` stay two blocks

Principle 9 requires the durable event and its query projection to commit
atomically, and that is one transaction — but the transaction belongs entirely
to `EventRepository`, which owns both tables. `EventBroker` never participates
in it.

The rule that keeps them separable: **the broker only ever publishes an event
the repository has already committed.** Publication is fan-out over a
`Channel<Event>`, serialised, after the fact. A subscriber therefore cannot
observe an event that a crash would un-happen, which is the property principle 9
is actually protecting.

Merging them would put a fan-out loop inside a transaction, which is worse.

### 3. The tool snapshot is a type

`ToolSnapshot`, immutable, materialised once per turn at step 5 of the turn
sequence.

Discipline was the alternative and it is not enforceable — "do not mutate the
registry mid-turn" is a comment, whereas a snapshot that has no mutators is a
compiler error. Principle 4 wants an immutable registry within a turn, and this
is the cheapest way to actually get it.

`ToolRegistry` was the mutable side that produced the snapshot. It is gone: once
a tool instance belongs to one `AgentSession`, the session's tool set is built
once from its `IToolFactory` list and never changes, so there is no mutable side
left for a registry to be. The snapshot is materialised from that fixed set,
and principle 4 holds for the same reason it did before.

### 4. The provider owns credential refresh

`ICredentialStore` is storage and nothing else: get, set, delete, by provider
id. It knows nothing about OAuth, expiry, or refresh.

`ILLMProvider` owns refresh, because only the provider knows what its own token
lifecycle is — when a token expires, what endpoint renews it, what a 401 means.
It reads the credential, refreshes when it must, writes the new one back.

So the arrow does not reverse: `ILLMProvider` → `ICredentialStore`, as drawn.
This keeps the store a dumb, trivially testable boundary and keeps
provider-specific knowledge inside the provider, which is where the
extension boundary already is.

### 5. `EnhancedCli` decomposes at M7, not now

Deferred deliberately, with a trigger rather than a vague "later": it is decided
when M7 is planned, and not before. It is the largest block, it renders
everything else, and its internal shape is guessable only once the things it
renders exist.

This is the one question whose honest answer is "not yet". Recording it as
deferred with a trigger is different from leaving it open — nothing between here
and M7 depends on it, so nothing is blocked.

`BasicCli` has the opposite property and is ranked early on purpose: the event
contract gets tested before a TUI can paper over a gap in it.
