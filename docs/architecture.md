# Top-Level Architecture

**Status: draft, first pass. Not yet reviewed.** This is the level-1 deliverable
of the plan gate (MIGRATION.md §0). It names the top-level classes and how they
connect. It does not yet decompose any of them, and no component may be ported
until its entry in [components.md](components.md) is filled in from this.

Derived by reading the Go tree, not invented: the block set below is
`app.App`'s field list plus what `httpapi.DomainBackend` carries. Where the Go
name is a package-level `Service`/`Registry`/`Manager`, the C# name says what it
manages, because in C# the namespace no longer disambiguates it.

## The blocks

```text
+---------------------------------------------------------------------------+
| ENTRY     Program             CommandDispatcher      TerminalChat         |
+---------------------------------------------------------------------------+
| TRANSPORT HttpServer          InProcessTransport     ApiBackend           |
+---------------------------------------------------------------------------+
| ROOT      ParrotApplication                                               |
+---------------------------------------------------------------------------+
| DOMAIN    AgentSession        AgentRegistry          TaskManager          |
|           Compactor           SystemContextBuilder                        |
+---------------------------------------------------------------------------+
| TOOLS     ToolRegistry        ITool                  PermissionBroker     |
|           QuestionBroker      ProcessRunner          WebFetcher           |
+---------------------------------------------------------------------------+
| PROVIDERS ProviderRegistry    ILLMProvider           ICredentialStore     |
+---------------------------------------------------------------------------+
| STORAGE   AgentSessionStore   EventBroker            EventRepository      |
|           Configuration       StatePaths                                  |
+---------------------------------------------------------------------------+
```

## The request path

Local mode and serve mode meet at `ApiBackend` and are identical below it.
That is principle 11: the local CLI and remote clients use one contract.

```text
                  +-------------------+
                  |      Program      |   Main; PosixSignalRegistration
                  +---------+---------+   for SIGINT and SIGTERM
                            |
                            | await
                            v
                  +-------------------+
                  | CommandDispatcher |   the single top-level Run
                  +----+---------+----+
                       |         |
            local mode |         | serve mode
                       v         v
          +----------------+  +----------------+
          |  TerminalChat  |  |   HttpServer   |
          +--------+-------+  +--------+-------+
                   |                   |
                   v                   |
          +----------------+           |   no socket is opened
          |   InProcess    |           |   in local mode
          |   Transport    |           |
          +--------+-------+           |
                   |                   |
                   +---------+---------+
                             |
                             v
                    +-----------------+
                    |   ApiBackend    |   the HTTP and SSE contract
                    +--------+--------+
                             |
                             v
                     ( the domain, below )
```

## The dependency tree

Who calls whom. Read the indentation as "depends on".

```text
ApiBackend
 |
 +-- AgentSession ................ a rich object, not a record plus a service.
 |    |                           it owns its state, its drain, and its turns
 |    |                                             [principles 2, 3, 4, 6]
 |    +-- AgentSessionStore ...... loads and persists it
 |    +-- EventRepository
 |    +-- SystemContextBuilder ... sampled only at a safe turn boundary
 |    +-- Compactor
 |    +-- AgentRegistry ......... agent profiles; also spawns and owns
 |    |                           child sessions
 |    |    +-- AgentSession ..... (recurses: a child session)
 |    |
 |    +-- ToolRegistry .......... immutable snapshot per turn
 |    |    +-- ITool  <<extension boundary>>
 |    |         +-- ProcessRunner .. sandboxed exec, fails closed
 |    |         +-- WebFetcher
 |    |
 |    +-- ProviderRegistry
 |    |    +-- ILLMProvider  <<extension boundary>>   stateless
 |    |         +-- ICredentialStore  <<extension boundary>>
 |    |
 |    +-- TaskManager ........... the task tree
 |
 +-- TaskManager
 +-- PermissionBroker ............ authorises an operation, not a tool name
 +-- QuestionBroker
 +-- EventBroker ................. serialised publication    [principle 9]
      +-- EventRepository
           +-- AgentSessionStore

ParrotApplication                  the composition root: constructs and owns
 |                                 every singleton above, explicitly, by hand
 +-- Configuration ............... merged once, immutable thereafter
 +-- StatePaths
      +-- AgentSessionStore ...... one database per session, never two hosts
```

## The Run tree

Whose lifetime bounds whose. This is a different graph from the one above, and
`AGENTS.md` makes it the load-bearing one: cancellation flows down, nothing
flows up, and a parent `Run` does not return while a child `Run` is in flight.

A block absent here is passive — it is called, it does not run.

```text
CommandDispatcher.Run                       returns => the process exits
 |
 +-- HttpServer.Run ....................... serve mode only
 +-- TerminalChat.Run ..................... local mode only
 +-- AgentRegistry.Run .................... hosts spawned child sessions
 |    |                                     a child outlives the turn that
 |    |                                     spawned it, so it is not nested
 |    +-- AgentSession.Run ................ one child session
 |
 +-- AgentSession.Run ..................... one drain per session
      |                                     a turn is a loop iteration here,
      |                                     not a nested Run
      +-- ProcessRunner.Run ............... one child process
```

There is no `Stop` anywhere. Shutdown is cancellation of the token `Program`
holds; `IDisposable` releases handles after `Run` has already returned.

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
| 2 | `AgentSessionStore` | `store`, `workspace` |
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
| 9 | `AgentSession` | `session` (all of it), `agent` (runner and coordinator) |
| 9 | `AgentRegistry` | `agent` (registry, provider resolution), `subagent` |
| 10 | `ApiBackend` | `api/v1`, `httpapi` (backend half) |
| 10 | `InProcessTransport` | `transport`, `client` |
| 11 | `HttpServer` | `httpapi` (server, routes) |
| 11 | `ParrotApplication` | `app` |
| 12 | `CommandDispatcher`, `TerminalChat` | `cli`, `terminal`, `diagnostics` |

`AgentSession` sits at rank 9, not at the rank 4 its state alone would suggest,
because it owns the drain and the turn, and a turn needs the tool registry and
the providers. That is the cost of collapsing the coordinator and the runner
into it, and it is why `AgentSessionStore` is ranked 2: session state becomes
persistable long before `AgentSession` itself can be built.

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
6. **`TerminalChat` is 4.6k lines of `terminal` plus 9.3k of `cli`.** Almost
   certainly several trees. It is ranked last so the shape can be decided once
   everything it renders exists.
