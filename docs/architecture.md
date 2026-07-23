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
| DOMAIN    Session             TurnRunner             AgentRegistry        |
|           TaskManager         SubagentManager        Compactor            |
|           SystemContextBuilder                                            |
+---------------------------------------------------------------------------+
| TOOLS     ToolRegistry        ITool                  PermissionBroker     |
|           QuestionBroker      ChangeSet              ProcessRunner        |
|           WebFetcher                                                      |
+---------------------------------------------------------------------------+
| PROVIDERS ProviderRegistry    IProvider              ICredentialStore     |
+---------------------------------------------------------------------------+
| STORAGE   SessionStore        EventBroker            EventRepository      |
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
 +-- Session ..................... a rich object, not a record plus a service
 |    |                            it owns its own state and its own drain
 |    |                                                       [principle 2]
 |    +-- SessionStore ........... loads and persists it
 |    +-- EventRepository
 |    |
 |    +-- TurnRunner ............. one provider turn          [principle 3]
 |         |
 |         +-- SystemContextBuilder ... sampled only at a safe boundary
 |         +-- Compactor
 |         +-- AgentRegistry
 |         |
 |         +-- ToolRegistry ....... immutable snapshot per turn
 |         |    +-- ITool  <<extension boundary>>
 |         |         +-- ChangeSet ...... transactional file edits
 |         |         +-- ProcessRunner .. sandboxed exec, fails closed
 |         |         +-- WebFetcher
 |         |
 |         +-- ProviderRegistry
 |         |    +-- IProvider  <<extension boundary>>
 |         |         +-- ICredentialStore  <<extension boundary>>
 |         |
 |         +-- TaskManager ........ the task tree
 |              +-- SubagentManager
 |                   +-- Session   (recurses: a child session)
 |
 +-- TaskManager
 +-- PermissionBroker ............ authorises an operation, not a tool name
 +-- QuestionBroker
 +-- EventBroker ................. serialised publication    [principle 9]
      +-- EventRepository
           +-- SessionStore

ParrotApplication                  the composition root: constructs and owns
 |                                 every singleton above, explicitly, by hand
 +-- Configuration ............... merged once, immutable thereafter
 +-- StatePaths
      +-- SessionStore ........... one database per session, never two hosts
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
 +-- Session.Run ......................... one drain per session
      |
      +-- TurnRunner.Run .................. one provider turn
           |
           +-- ProcessRunner.Run .......... one child process
           +-- SubagentManager.Run ........ one child session
                |
                +-- Session.Run             (recurses)
```

There is no `Stop` anywhere. Shutdown is cancellation of the token `Program`
holds; `IDisposable` releases handles after `Run` has already returned.

## Session

`Session` is the one block that must not be a record plus a service. Upstream
`session.Session` is a twelve-field struct with **no methods**, and its behaviour
lives in four other places — `Service`, `GoalService`, `TodoService`, and
`agentSession`/`drainState` inside the agent coordinator. That is the anemic
model `AGENTS.md` rejects, and MIGRATION.md §5 names it by that exact shape.

It carries a lot of state, which is why it is the block most likely to be got
wrong. Everything below belongs to one session and nothing outside it reads
any of it:

```text
Session
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

`Session.Run` performs this sequence per turn, in order. It is the safe
provider-turn boundary, and context sources are sampled nowhere else:

```text
1. initialize or reconcile the context epoch
2. promote eligible input          steer: at this boundary
                                   queue: when the turn would otherwise stop
3. resolve the agent and model
4. load active history
5. materialize an immutable tool registry snapshot
6. compact history if required
7. invoke the provider
```

## Blocks to Go packages

Rank is migration order. A block may not be built before anything it depends on.

| Rank | C# class | Absorbs (`internal/…`) |
| --- | --- | --- |
| 1 | `StatePaths` | `appdirs`, `project`, `id`, `atomicfile`, `processidentity` |
| 1 | `Configuration` | `config`, `mode` |
| 2 | `SessionStore` | `store`, `workspace` |
| 2 | `EventRepository` | `event` (persistence half) |
| 3 | `EventBroker` | `event` (broker, stream, subscription) |
| 3 | `ICredentialStore` | `auth`, `security` |
| 4 | `TaskManager` | `task`, `status`, `monitor` |
| 5 | `IProvider`, `ProviderRegistry` | `provider`, `protocol` |
| 5 | `PermissionBroker`, `QuestionBroker` | `permission`, `question` |
| 6 | `ProcessRunner` | `process` |
| 6 | `ChangeSet` | `change` |
| 6 | `WebFetcher` | `webfetch` |
| 7 | `ITool`, `ToolRegistry` | `tool` |
| 7 | `SystemContextBuilder` | `systemcontext`, `skill`, `command` |
| 8 | `Compactor` | `compaction` |
| 8 | `AgentRegistry` | `agent` (registry, provider resolution) |
| 9 | `TurnRunner` | `agent` (runner) |
| 9 | `Session` | `session` (all of it), `agent` (coordinator) |
| 9 | `SubagentManager` | `subagent` |
| 10 | `ApiBackend` | `api/v1`, `httpapi` (backend half) |
| 10 | `InProcessTransport` | `transport`, `client` |
| 11 | `HttpServer` | `httpapi` (server, routes) |
| 11 | `ParrotApplication` | `app` |
| 12 | `CommandDispatcher`, `TerminalChat` | `cli`, `terminal`, `diagnostics` |

`Session` sits at rank 9, not at the rank 4 its state alone would suggest,
because it owns the drain and the drain needs `TurnRunner`. That is the cost of
collapsing the coordinator into it, and it is why `SessionStore` is ranked 2:
session state becomes persistable long before `Session` itself can be built.

## Open questions

Resolve these before filling in `components.md`; each one moves a boundary.

1. **`Session` and `TurnRunner` reference each other.** `Session` owns the drain
   and starts turns; a turn reads and appends to session state. As drawn that is
   a cycle. It breaks if a turn becomes a `Turn` object that `Session`
   constructs and hands what it needs — which principle 3 already argues for,
   since it calls a provider turn an explicit, cancellable boundary, and a
   boundary with a lifetime is a type. Not done, because it goes beyond
   collapsing the services and wants a decision.
2. **Does `Session` sub-divide?** It owns ten groups of state (above), which is
   a lot for one type even when the type is correctly rich. Todos and goals are
   the obvious candidates for owned sub-objects — `Session.Todos` rather than a
   `TodoService` — but that is a decomposition question for level 2, not a
   reason to hand them back to a service.
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
