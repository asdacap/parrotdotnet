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

A model selection is either a configured model alias or a canonical
`provider/model[/effort-variant]` selector. The provider is the first path
segment, and the model may itself contain slashes. The refreshed provider
catalog supplies autocomplete and model metadata, but it is not an allowlist:
an unlisted model ID is passed to the provider and can fail when called. The
optional final segment is treated as an effort variant only when the complete
remainder is not an exact catalog model ID. This makes selectors stable even
when a provider offers slash-containing model IDs.

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

Parrot writes an agent-readable `predefined_config.yaml` alongside the
user-owned `config.yaml`. The user file is recursively layered over the
predefined defaults and is never rewritten except by an interactive setting.

Profiles are configured under `profiles`. `build`, `plan`, and `query` are
foreground modes, while `explorer`, `review`, `worker`, and `thinker` are child
profiles selected by `agent_spawn.agent`. `default_profile` must name a
foreground profile and is used when no mode is selected explicitly. The
foreground-mode RPC and slash-command surfaces list only foreground profiles.

Every profile has a nonempty `prompt`, `usage`, and `hard_rules` sequence; a
positive `max_turns`; a nonnegative `recursion_limit`; boolean `read_only` and
`is_user_agent`; and optional ordered `sandbox_rules`. A child profile can
recur only up to its selected profile's recursion limit. Profile sandbox rules
replace that profile's default list; top-level `sandbox_rules` apply to every
profile.

```yaml
default_profile: build
profiles:
  worker:
    usage: Delegate independently scoped implementation work.
    max_turns: 64
    allowed_tools:
      - read
      - apply_patch
```

`allowed_tools` has three meanings: omit it (or use `null`) to retain every
otherwise available tool, use `[]` to offer none, or list exact tool IDs to
offer only those tools. The same filtered set is both sent to the model and
used for execution. `explore` is accepted as a compatibility alias for the
canonical `explorer` child profile.

The plan foreground profile receives its private plan-artifact location and
runtime-only write permission from Parrot; child profiles never inherit this
capability. Legacy `profiles.<id>.status` input is accepted and ignored for
compatibility. It is not profile guidance and is never injected into a prompt.

## Model Aliases

Model aliases give stable names to model selectors. The effective configuration
combines these four predefined aliases with the `model_aliases` map in the
configuration file: `low_llm`, `medium_llm`, `high_llm`, and `xhigh_llm`.
Their predefined `usage` values are:

- `low_llm`: `mechanical, single file task, text or code processing when no suitable cli tool available.`
- `medium_llm`: `Decently capable, specific clear task, component level task, two or three file window`
- `high_llm`: `General purpose, agent spawner, tactical decision making and planning, debugging, colaborator`
- `xhigh_llm`: `Strategic work spanning multiple modules or parties, ambiguous or open-ended requirements, hard debugging or optimization, and high-level planning where cheaper models are insufficient.`

A configured entry can override any predefined field without losing its default
metadata, and can add another alias.

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
executes that alias's resolved canonical target. The route is resolved once at
the beginning of each turn. Retargeting an alias affects the next turn only;
an active turn, including its tool rounds, continues to use its captured
canonical route and matching prompt configuration.

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

Alias listing and configuration belong to the server that executes turns. A
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
active defaults; do not edit it.

Put personal settings in `config.yaml` in the same directory. It is never
created or overwritten by loading configuration. Parrot recursively merges its
mapping over `predefined_config.yaml`: nested mappings combine by key, while
scalars and sequences replace their corresponding defaults. Thus a
`model_aliases.low_llm.model_string` entry can override that target without
repeating its predefined usage. The predefined file contains all seven profile
definitions, so a nested `profiles.<id>` mapping can override one profile field
while inheriting every other field from the active default.

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
then a model, `/model-alias` to configure a predefined or custom alias, `/mode`
to select a mode, `/clear` to configure a fresh session, or `/auth` to manage
credentials. Text after the command name is ignored; the wizard always asks for
the complete selection. Escape or Ctrl-C dismisses an enhanced wizard without
applying partial changes.

The basic CLI prints choices and reads them as lines. The enhanced CLI replaces
only its live input area with a filterable picker, so an active turn's output,
activity, and modeline remain visible. Use Up/Down to navigate, type to filter,
Enter to accept, and Escape or Ctrl-C to dismiss. In the enhanced chat prompt,
type `/` to show slash-command suggestions, type to filter them, use Up/Down to
highlight one, and press Tab to complete it; Enter then runs the completed
command.

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
