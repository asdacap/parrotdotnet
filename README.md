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
event stream. They share no code with each other; see
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

## Build And Run

Everything runs inside the flake's dev shell; there is no supported way to build
this repository against an ambient SDK.

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
