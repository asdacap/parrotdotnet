# Parrot Coder (.NET)

A rewrite of [parrot-coder](https://github.com/asdacap/parrot-coder) — a
local-first coding agent — from Go to C# on .NET Native AOT.

The goal is the same product: one self-contained binary with no runtime
dependency, scrollback-preserving terminal chat, durable SQLite sessions and
event history, OAuth and OpenAI-compatible providers, permission-bound tools,
session compaction, and bounded web fetching. The Go implementation is the
specification; this repository is the implementation.

The whole product is a gRPC server. The two CLIs — a deliberately minimal one
and a full terminal UI — sit entirely behind it as clients, consuming one flat
event stream. They share command workflows but no presentation code; see
[docs/architecture.md](docs/architecture.md).

**MCP and transactional file edits are out of scope.** Upstream's
`internal/mcp` and the all-or-nothing apply in `internal/change` are not ported;
see [docs/components.md](docs/components.md).

**Status: scaffold.** The build, the linter, the test harness, and the AOT
publish path work end to end. No upstream component has been migrated yet.
See [MIGRATION.md](MIGRATION.md).

## Why Native AOT

The Go original ships as a single static binary that starts instantly. Anything
less would be a regression, so Native AOT is a hard requirement rather than an
optimisation: no JIT warm-up, no shared framework to install, and a startup cost
in the low milliseconds. It constrains the code that can be written — no runtime
reflection over unannotated types, no `Assembly.Load`, no unbounded generic
virtual dispatch — and those constraints are enforced at build time by the
analyzers in `.editorconfig`, not discovered at publish time.

## Layout

```text
Parrot.slnx
src/
  Parrot.Core/        Everything that is not the entry point. Upstream Go
                      packages become namespaces here (Parrot.Agent,
                      Parrot.Tool, ...), not separate assemblies.
  Parrot.Cli/         The `parrot` executable. AOT-published.
test/
  Parrot.Cli.Tests/   TUnit. One test project per src project.
docs/
```

Assemblies are few on purpose: build time and AOT link time both scale with
project count, and boundaries between components are enforced by namespace
discipline and analyzer rules rather than by `ProjectReference` graphs. A new
assembly needs a reason recorded in `MIGRATION.md`.

## Model Selection

A model selection is a canonical `provider/model[/effort-variant]` selector.
The provider is the first path segment; the model is resolved against that
provider's catalog and may itself contain slashes. The optional final segment is
therefore treated as an effort variant only when the complete remainder is not
an exact model ID. This makes selectors stable even when a provider offers
slash-containing model IDs.

A variant has a stable catalog name and a provider-facing `reasoning_effort`
value. They are intentionally distinct: selecting `high`, for example, can map
to the provider effort `xhigh`. A bare `provider/model` leaves reasoning effort
unset so the provider chooses its default. Interactive `/model` preserves a
compatible current effort when possible; otherwise it uses the target model's
first listed variant, or clears the effort for a model without variants.

`/model` and `/effort` persist the complete canonical selector through the
shared configuration. `/effort NAME` selects one of the active model's listed
variants; bare `/effort` presents those variants in provider order. `--model`
is per invocation and accepts the same complete selector. `--variant NAME` is a
deprecated startup-only override: it replaces any selected suffix after
validation against the selected model and does not persist.

The gRPC model-list response carries ordered model-variant metadata
additively. Each entry contains the stable `name` and mapped
`reasoning_effort`; session state remains the single canonical model selector,
not a separate variant field.

Provider request dialects deliberately differ. Responses requests place a
selected effort and automatic summary under
`"reasoning":{"effort":"…","summary":"auto"}`. Chat Completions requests
place it at top level as `"reasoning_effort":"…"`. Both omit their effort field
for a bare model selection.

## Build And Run

The quickest way to run it, no dev shell needed:

```sh
nix run . -- version
nix run . -- chat "hello"
```

That builds the portable, framework-dependent binary. Development and the
Native AOT publish both happen inside the dev shell; there is no supported way
to build this repository against an ambient SDK.

## Interactive slash commands

Slash commands are interactive wizards. Enter `/model` to select a provider and
then a model, `/mode` to select a mode, `/clear` to configure a fresh session,
or `/auth` to manage credentials. Text after the command name is ignored; the
wizard always asks for the complete selection. Escape or Ctrl-C dismisses an
enhanced wizard without applying partial changes.

The basic CLI prints choices and reads them as lines. The enhanced CLI replaces
only its live input area with a filterable picker, so an active turn's output,
activity, and modeline remain visible. Use Up/Down to navigate, type to filter,
Enter to accept, and Escape or Ctrl-C to dismiss.

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

This is the same tax the Go original pays with `vendorHash`.

## Static musl

The shipped binary is linked statically against musl. It has no interpreter and
no `NEEDED` entry — nothing to install, and it runs on any Linux regardless of
which libc is present, which is the property the Go original got from
`CGO_ENABLED=0`.

```console
$ ldd parrot
        statically linked
```

It costs about 1.4 MB over a glibc-dynamic build (5.8 MB against 4.4 MB), and
both keep their symbols, matching the Go build's `dontStrip` so a core dump from
a release binary is still usable.

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
part of the build. See [docs/style.md](docs/style.md) for what is enforced and
how to add an exception.

## For Migration Agents

Read [MIGRATION.md](MIGRATION.md) before writing any code. It is normative, not
advisory. In particular: no component may be ported until the high-level
component map in `docs/components.md` names it.

- [docs/architecture.md](docs/architecture.md) — the blocks and how they connect
- [docs/plan.md](docs/plan.md) — milestones, ordered by risk
- [docs/components.md](docs/components.md) — the gate, not yet written
