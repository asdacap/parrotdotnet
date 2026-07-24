# Component Map

**Status: not written.** This is the Phase 0 deliverable described in
MIGRATION.md §0. Until it names a component, that component may not be ported.

The level-1 view — which blocks exist, how they connect, and their migration
rank — is in [architecture.md](architecture.md), drafted and awaiting review.
This file is level 2: one filled-in entry per block from that diagram. Resolve
the open questions at the end of `architecture.md` first; each one moves a
boundary, and moving a boundary after an entry is written means rewriting it.

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
| Transactional file edits | `internal/change` (1172 lines): the all-or-nothing apply, rollback, and `FileStore`/`FileState` machinery | Dropped entirely by decision, 2026-07-24. Tools write files directly. Patch *parsing* survives, folded into the tool that needs it, because `apply_patch` cannot function without it. |
| Windows support | Windows paths, credential storage, process trees, terminal behaviour | Upstream targets macOS and Linux; so does this. |

## Entry template

One section per component. An entry is not complete until every field is filled.

### `<Namespace>`

- **Absorbs**: the upstream Go packages this component replaces.
- **Owns**: the state whose lifetime and mutation this component is solely
  responsible for. If two components claim the same state, the map is wrong.
- **Inbound contract**: what callers may ask of it, and the invariants it
  promises. Name the architecture principles from `docs/architecture.md` that
  it is responsible for upholding.
- **Outbound contract**: what it requires of its dependencies, expressed as the
  abstraction it depends on rather than the concrete type it happens to get.
- **Extension boundary**: whether this is one of the four replaceable I/O
  boundaries upstream declares. Upstream names five — provider protocols, secret
  storage, tools, MCP transports, formatters — and MCP is dropped here, leaving
  four. If not, it uses concrete types — see MIGRATION.md §5.
- **Rank**: migration order. A component may not be migrated before anything it
  depends on.
- **Out of scope**: upstream behaviour deliberately not carried over, with the
  reason. Tests covering it are deleted, not skipped.

## Assemblies

Namespaces are the unit of separation in this repository, not assemblies. The
current set is `Parrot.Core` and `Parrot.Cli`. Adding a third project requires a
reason recorded here.

| Assembly | Reason |
| --- | --- |
| `Parrot.Core` | Everything that is not the process entry point. |
| `Parrot.Cli` | The AOT-published executable. Separated so that `Parrot.Core` can be referenced by a test host without dragging in the entry point. |
| `Parrot.Analyzers` | Build tooling, not product code. Repository-specific lint rules that no shipped analyzer expresses (`PARROT0001`–`PARROT0003`). Must be a separate `netstandard2.0` project because it runs inside the compiler; referenced as an analyzer, so it never reaches the runtime or the AOT link. |
