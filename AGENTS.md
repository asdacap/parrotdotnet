## Guideline

- No comment unless necessary
- Change in design to clean original code is allowed.
- Dependency injection.
- Prefer rich domain object rather than anemic domain model.
  - For example, if something is agent session specific, create an instance per agent session and store in the same DI
  scope as the agent session.
- No dotnet events.
- No subclass unless necessary.
- No subinterface unless necessary.
- Interfaces and implementations are fine and in fact preferable.
- NEVER turn of lint rules repo wide or project wide. Exception is test project can have lint rules disabled AFTER user
  explicit permission.
- Suffix the class name properly. If a class is an event, suffix it with Event.
- Do not make a method/function just to create another class, unless that method is on that class. Dont do CreateEvent(something). Do Event.Create(something).
- Class name are noun and declarative. Method name are imperative. Its an action (unless its the class constructor). So
  method name MUST have action please, come on man...
- Reduce coupling, as in the public method count.
- Dont share or deduplicate unnecessarily especially at the expense of coupling and code indirection
- Prefer async workflow over manual state machine.
- No optional parameter.
- Robust lazy validation. Try not to validate early, rather make later validation handling robust.
- Lazy materialization. This follows the same spirit as lazy validation. Do not pre-optimize state, only compile them
right before the information is needed.
- A user session (and all its component including all agent sessions) data is scoped within that user session. Do not
read or write other user session data.
- Tools or any permission check should check the security profiles rather than specific directory.
- Do not make unnecessary change in prod for test.
- No method overload. Use different method name instead.
- Using `as` or typecheck tend to be an antipattern. Rather than a T1 as T2, make a T3 or expand T1 to include T2 interface.
- Configuration are like the atomic representation of code. Making more code just to assert config exactly are
duplicated code, so dont make unit test like that. This is not the same as making a unit test that test behavior of
config value.
- Every interaction with the agent, as in the prompt MUST be templated and made configurable via predefined_config for
easy review and modification.

## Async lifecycle

- Rather than something like:
```
using var cls = new Something();
cls.Start();
cls.Stop();
```

Just do.

```
using var cls = new Something();
await cls.Run(cancellation);
```
- In fact, there should probably be a single top level `Run` what is the parent of all other run.

## SOLID preferences.

- Single Responsibility: Eh... 
- Open Close: Yes. Something tend to be wrong when this is not the case. 
- Liskov Substitution: Or just straight up no inheritence. Just compose.
- Interface Segregation: Eh...
- Dependency Inversion: Decent...

## Environment

Everything runs inside the flake's dev shell. There is no supported way to build
against an ambient SDK, and Native AOT will not link outside it.

```sh
nix develop
```

`nix run . -- <args>` works without the dev shell, but builds the portable
binary rather than the AOT one. A package reference change requires
regenerating `nix/deps.json`; see README.

**`git add` a new file before `nix build`/`nix run`.** A flake sees only
git-tracked files, so an untracked `.cs` is dropped from the build and surfaces
as a spurious "type not found", not "you forgot to add a file". The dev-shell
gates below read the working tree directly and never hit this, so a green
`dotnet build` can still fail under Nix until the file is tracked.

## Gates

```sh
dotnet build Parrot.slnx -c Release
dotnet test Parrot.slnx -c Release
dotnet format Parrot.slnx --verify-no-changes
dotnet publish src/Parrot.Cli/Parrot.Cli.csproj -c Release -r linux-musl-x64
nix flake check    # nix formatting only; see the comment in flake.nix
```

Warnings are errors in every project. `AnalysisLevel` is `latest-all` and the
trim/AOT analyzers run everywhere, so a build that succeeds is a build that is
AOT-clean.

## The rules that get broken most often

- **Do not deviate from the plan.** If the plan is wrong, stop and say so rather
  than improvising a fix.
- **Do not port a component the map does not name.** Fix the map first.
- **Never turn a rule off.** Not inline, not per-file, not repository-wide.
  `#pragma warning disable` and `[SuppressMessage]` are not permitted, and
  neither is adding to the disabled block in `.editorconfig`. Change the code.
  A rule firing on code you believe is correct usually means the code is more
  public, more mutable, or more general than it needs to be.
- **No reflection, no reflection-based JSON.** Source generation only.
- **No stubs that return plausible values.** An unimplemented path throws.
- **Matching an id to change behaviour is an antipattern.** Put it on the
  abstraction.
- **Prefer an interface over a config flag**, and do not add an interface with a
  single implementation just to enable a mock.
- **A failing ported test is skipped and reported**, never weakened or deleted.

## Commits

A commit message for a migrated component names the upstream Go packages it
absorbed. Check `git status` for unexpected changes before committing; if there
are any, do not commit.

## Analogy

A good software is like a nice garden. There are distinct clear trees, each tree can be of different shape, 
and size, but its clearly itself, and there are roads to clearly move around the garden. The purpose of the garden
is the tree, not the road, but without the road, its hard to plant tree. 

## Nomenclature

- A user-conversation-turn is from first message to final message.
- A tool-call-cycle is the provider request -> response.
