# Migration Rules

Normative rules for porting [parrot-coder](https://github.com/asdacap/parrot-coder)
from Go to C# / .NET Native AOT. These are not suggestions. A change that
violates a rule is reverted, not discussed.

The Go tree is the specification. Read it at `~/repo/parrot-coder`. Its
`docs/architecture.md` states twelve architecture principles; those principles
survive the port unchanged. Its `docs/` directory is the behavioural contract.

---

## 0. The plan gate

**No component may be ported before `docs/components.md` names it.**

The migration is top-down. Before any behaviour is written:

1. Identify the top-level components of the Go system and their contracts —
   what each one owns, what it depends on, and what crosses its boundary.
2. Record them in `docs/components.md`: one entry per component, listing the
   upstream Go packages it absorbs, its owned state, its inbound and outbound
   contracts, and its migration order rank.
3. Get that document reviewed before descending a level.

That is milestone 0 in [docs/plan.md](docs/plan.md), which sequences everything
after it.

Then descend: for each component, decompose it into sub-components the same
way, and only then write code. A component's parent must be specified before
the component is.

If a Go package does not fit any named component, that is a finding about the
component map. Fix the map; do not invent an unlisted home for the code.

## 1. Fidelity

This is a rewrite, not a drop-in replacement. `AGENTS.md`: *change in design to
clean original code is allowed.* There is no requirement to read a state
directory written by the Go binary, or to serve a client built against it.

### Load-bearing invariants

These survive because they are **correctness**, not compatibility. Breaking one
is a bug in any implementation, in any language:

- **Permission semantics.** A permission authorises a canonical operation, not a
  tool name (principle 7). Authorisation and OS containment stay separate
  concerns (principle 8).
- **The sandbox fails closed.** A shell command does not run when the sandbox is
  unavailable. Not a warning, not a fallback.
- **One machine writes one user session's database.** The state directory may
  sit on NFS, and a shared filesystem cannot be assumed to provide working
  locks, so the division is structural rather than lock-based: a working
  directory is a host-local name. No `-shm` or `-wal` file may ever appear under
  the state directory, so WAL is out. Listing reads the published projection,
  never another host's database. A claim uses `link()`, not `rename`. Repair
  never ranges across user sessions. `config.yaml` is the single deliberate
  exception — shared, whole-file, atomic by rename, and therefore the only place
  global mutable state such as flags may live. Read the `UserSession` section of
  `docs/architecture.md` before touching any of this; the reasoning is subtle
  and the failure is silent corruption.
- **A prompt is durable before execution, and a tool call is durable before its
  side effects begin** (principles 1 and 5).
- **Event ordering.** Durable events and their query projections commit
  atomically (principle 9); publication is serialised.
- **A context epoch is immutable within its lifetime** (principle 4).
- **No claim of exactly-once provider execution** after an uncertain process
  failure.

### Free to change

Everything else, provided the divergence is recorded in `docs/components.md`:
internal design and type decomposition, file layout, concurrency primitives
(§3), error representation (§4), the wire contract, the on-disk schema, the
configuration format, and CLI text. Where the Go shape is worse in C#, take the
better shape.

Two cautions. First, *record* the divergence — an undocumented wire change is
indistinguishable from a porting mistake when a test fails six components later.
Second, a shape that looks accidental in Go is often load-bearing; check the
upstream tests before deciding it was arbitrary.

## 2. Native AOT is a hard constraint

The product is a single self-contained binary. Therefore:

- No runtime reflection over types the trimmer cannot see. No
  `Assembly.Load`, no `Activator.CreateInstance(Type)` on a computed type, no
  `Type.GetType(string)`.
- JSON uses `System.Text.Json` **source generation** only. A
  `JsonSerializerContext` per contract; never a reflection-based overload.
- Every `IL2xxx` and `IL3xxx` diagnostic is an error and may not be suppressed.
  A `[RequiresUnreferencedCode]` or `[RequiresDynamicCode]` annotation on
  Parrot's own code is a design failure, not an escape hatch.
- A new NuGet dependency must be AOT-compatible and must be justified in the
  pull request. Prefer the framework, then a source-generated library, then
  nothing. The Go original depends on ten packages; beating that is the target.
- `dotnet publish -c Release -r linux-musl-x64` must succeed with zero
  warnings before a component is called done. The shipped binary is statically
  linked against musl, so a dependency that needs a shared library at runtime
  does not qualify as AOT-compatible here even if it compiles.

## 3. Concurrency

Go's model does not survive transliteration. Translate intent, not mechanism:

| Go | C# |
| --- | --- |
| `go f()` | `Task.Run` only when the work is CPU-bound; otherwise call the async method and hold the `Task` |
| `chan T` | `System.Threading.Channels.Channel<T>` |
| `select` | `await Task.WhenAny`, or a channel read with a `CancellationToken` |
| `context.Context` | `CancellationToken`, always the **last** parameter, always forwarded (CA2016 is an error) |
| `sync.Mutex` | `lock` for non-async sections; `SemaphoreSlim` when the critical section awaits |
| `sync.WaitGroup` | `Task.WhenAll` |
| `defer` | `using` / `await using`, or `try/finally` |

### Lifecycle

`AGENTS.md` fixes the shape. A component does not expose `Start`/`Stop`:

```csharp
using var component = new Something();
await component.Run(cancellationToken);
```

- **One top-level `Run`, and every other `Run` is a descendant of it.** In this
  repository that is `CommandDispatcher.Run`, awaited by `Program.Main`. The
  process exits when it returns.
- A component's work is bounded by the `Run` call that started it. A `Run` does
  not return while work it started is still in flight, so an abandoned `Task` is
  structurally impossible rather than merely discouraged.
- Shutdown is cancellation, not a second method. `Program` registers
  `PosixSignalRegistration` for `SIGINT` and `SIGTERM` — not
  `Console.CancelKeyPress`, which is a .NET event — and cancels the token.
- `IDisposable`/`IAsyncDisposable` releases handles. It does not stop work; the
  cancellation token does that, and `Run` has already returned by then.

This is the Go structure translated honestly: a goroutine's lifetime is bounded
by the function that spawned it and the context it was handed, and `Start`/`Stop`
pairs lose exactly that property.

### Rules

- Blocking on async (`.Result`, `.Wait()`, `GetAwaiter().GetResult()`) is
  forbidden outside `Program.Main`.
- Async methods return `Task`/`ValueTask`; streaming returns
  `IAsyncEnumerable<T>` with `[EnumeratorCancellation]`.
- The Go tree runs its suite under `-race` and treats a race as a release
  blocker. The equivalent gate here is that shared mutable state must be
  explicitly owned by one component with its synchronisation documented at the
  type.

## 4. Errors

- Go returns errors; C# throws. Do not port `(T, error)` as a result tuple
  except where the Go code branches on the error to produce different **user
  visible** behaviour — then model it as a result type, not an exception.
- Never throw `Exception`, `SystemException`, or `ApplicationException`
  (CA2201 is an error). Define a component-specific exception type.
- An exception crossing a component boundary must carry a message that is
  meaningful in the CLI, because it will end up there.
- A caught-and-reported exception at a process, provider, or tool boundary is a
  deliberate containment point. Mark it with a comment saying so.

## 5. Design rules carried over from upstream

`AGENTS.md` is the authority here; this section only says what each rule means
for a port from Go. Where the two disagree, `AGENTS.md` wins.

### SOLID, as weighted by `AGENTS.md`

| | Weight | What that means for the port |
| --- | --- | --- |
| Single Responsibility | *Eh* | Do not split a cohesive type to satisfy it. A Go package that is one clear thing becomes one clear type. |
| Open–Closed | **Yes** | The load-bearing principle. When a change forces edits across existing types, something upstream of it is modelled wrong — stop and fix the model. |
| Liskov Substitution | *Not applicable* | Because there is no inheritance to substitute into. Compose. |
| Interface Segregation | *Eh* | Do not shred an interface into role interfaces. A wide interface that matches one real seam beats four narrow ones nobody implements separately. |
| Dependency Inversion | *Decent* | Depend on the abstraction at the four extension boundaries; concrete types elsewhere. |

### Rules

- **Prefer an interface over a config flag.** A behavioural difference belongs
  in a type, not in a branch on a setting.
- **Matching an id to change behaviour is an antipattern.** If the UI renders a
  tool differently, that is a method on the tool abstraction — never
  `if (tool.Id == "exec_command")`.
- CLI event lines keep their icon or, failing that, their indentation.

From this repository's `AGENTS.md`:

- **An interface plus its implementations is the preferred shape**, including
  where there is one implementation today. This is the extension seam, and it is
  how a Go interface should land. Two things are discouraged, and neither
  contradicts that: *sub*-interfacing — do not derive `IThing` into
  `IBetterThing` to add a method, widen it or introduce a separate one — and
  extracting an interface whose only consumer is a mock.
- **No inheritance.** Not "little": none. Go has none and the port must not
  acquire any. Struct embedding becomes composition; a shared base class is the
  wrong answer to shared behaviour, a collaborator held as a field is the right
  one. Concrete types are `sealed`, and an abstract class is a design failure to
  raise rather than write.
- **No .NET `event`s and no `delegate` types.** Enforced by `PARROT0001` and
  `PARROT0002` (`tools/Parrot.Analyzers`). The upstream event broker is a
  stream, not a multicast delegate: use `Channel<T>` or `IAsyncEnumerable<T>`,
  which also preserves the ordering guarantee that principle 9 of
  `docs/architecture.md` depends on — a `+=` subscriber list gives no delivery
  ordering across subscribers and no backpressure. A Go `func` field becomes an
  interface, not a named delegate; `Func<>`/`Action<>` remain fine for a local
  callback.
- **One top-level symbol per file, and the file is named after it.** Enforced by
  SA1402, SA1403, and SA1649. A Go file holding six related types becomes six
  C# files. Do not preserve upstream file boundaries at the cost of this rule.
- **Rich domain objects, not an anemic model.** Behaviour lives on the type that
  owns the state. Go's package-level functions over structs translate to methods
  on the type, not to a static helper class. This is not hypothetical: upstream
  `session.Session` is twelve fields and no methods, with its behaviour spread
  across `Service`, `GoalService`, `TodoService`, and the agent coordinator's
  `agentSession`. Porting that shape reproduces the defect. See the
  `AgentSession` section of `docs/architecture.md`.
- **Dependency injection, but no IoC container.** Dependencies are passed to
  constructors — primary constructors, per `docs/style.md` — and the object
  graph is composed explicitly in `Parrot.Cli`. No `IServiceCollection`, no
  runtime service resolution. This is also an AOT requirement: container
  registration by scanning is exactly the reflection §2 forbids.
- **No comment unless necessary.** A comment explains why, or it is deleted.
  Restating the code is worse than silence, and XML doc generation is off.

## 6. Tests

- TUnit. One test project per src project, named `<Project>.Tests`.
- **Port the Go tests.** They are the conformance oracle — 24k lines of them.
  A component is not migrated until its upstream tests are represented.
- Prefer one parameterised test with `[Arguments]` over several near-identical
  tests. Combine related assertions into one test rather than splitting them.
- Test the unit's responsibility, not its dependencies'. Using a real
  implementation of a dependency is fine when it makes the test shorter.
- A ported test that fails because of a suspected bug is marked `[Skip("...")]`
  and reported. It is never deleted, and never weakened to pass.
- Do not test a Go behaviour that the component map says is out of scope; delete
  the test and record the decision in `docs/components.md`.

## 7. Prohibitions

- Do not add a project to the solution without recording the reason in
  `docs/components.md`. Namespaces are the unit of separation here, not
  assemblies.
- Do not turn an analyzer off — not inline, not per-file, not repository-wide.
  Change the code instead; see `docs/style.md` for why that is almost always
  the better fix.
- Do not widen `NoWarn`, lower `AnalysisLevel`, or turn off
  `TreatWarningsAsErrors` for any project.
- Do not commit commented-out code, `TODO` without an owner, or a stub that
  returns a plausible value. An unimplemented path throws
  `NotImplementedException`, so it fails loudly.
- Do not change a wire format, schema, or CLI string **silently**. Changing one
  is allowed (§1); leaving it unrecorded in `docs/components.md` is not.
- Do not break a load-bearing invariant from §1 for convenience. Those are not
  design preferences.
- Do not port Windows-specific behaviour. Upstream supports macOS and Linux;
  so does this.
- Do not deviate from the plan mid-implementation. If the plan is wrong, stop
  and say so.

## 8. Definition of done, per component

A component is migrated when all of the following hold:

1. `docs/components.md` lists it, and the entry matches what was built.
2. Its upstream Go tests are ported, passing, or explicitly skipped with a
   recorded reason.
3. `dotnet build -c Release` produces zero warnings.
4. `dotnet test -c Release` passes.
5. `dotnet format --verify-no-changes` reports no changes.
6. `dotnet publish src/Parrot.Cli/Parrot.Cli.csproj -c Release` succeeds with no
   trim or AOT warnings.
7. `nix flake check` passes.
8. The commit message names the upstream Go packages the component absorbed.
