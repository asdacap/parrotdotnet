# Parrot

Parrot is a local coding agent built with .NET Native AOT. Run it interactively,
send it a one-shot prompt, or host it as a service for remote clients. It
supports configurable model providers, persistent sessions, foreground modes,
and delegated child agents.

## Why Native AOT

Parrot ships as a self-contained executable that starts quickly and does not
require a shared .NET framework. Native AOT is therefore a hard requirement
rather than an optimisation: there is no JIT warm-up, and startup takes only a
few milliseconds. Native AOT constrains the code that can be written — no
runtime reflection over unannotated types, no `Assembly.Load`, no unbounded
generic virtual dispatch — and those constraints are enforced at build time by
the analyzers in `.editorconfig`, not discovered at publish time.

## Layout

```text
Parrot.slnx
src/
  Parrot.Core/        Everything that is not the entry point. Components are
                      organized as namespaces here (Parrot.Agent, Parrot.Tool,
                      ...), not separate assemblies.
  Parrot.Cli/         The `parrot` executable. AOT-published.
test/
  Parrot.Cli.Tests/   TUnit. One test project per src project.
docs/
```

Assemblies are few on purpose: build time and AOT link time both scale with
project count, and boundaries between components are enforced by namespace
discipline and analyzer rules rather than by `ProjectReference` graphs. A new
assembly should have a clear architectural reason.

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

Every profile has a nonempty `prompt` and `usage`; a positive `max_turns`; a
nonnegative `recursion_limit`; boolean `read_only`; boolean
`enforce_active_work_completion`; and optional ordered `sandbox_rules`.
`enforce_active_work_completion` controls whether the runtime requires active
work to be completed before the profile may finish a turn. The prompt is the
profile's only model-facing guidance. Because
configuration scalars replace rather than merge, overriding a profile prompt
replaces the complete predefined prompt, including its default behavioral
instructions; there is no separate `hard_rules` field to inherit or override.
Runtime restrictions such as `read_only`, tool filtering, and sandbox rules
remain independently enforced and are not weakened by prompt text. A child
profile can recur only up to its selected profile's recursion limit. Profile
sandbox rules replace that profile's default list; top-level `sandbox_rules`
apply to every profile.

```yaml
default_profile: build
profiles:
  worker:
    usage: Delegate independently scoped implementation work.
    max_turns: 64
    allowed_tools:
      - read
      - edit
```

`allowed_tools` has three meanings: omit it (or use `null`) to retain every
otherwise available tool, use `[]` to offer none, or list exact tool IDs to
offer only those tools. The same filtered set is both sent to the model and
used for execution. `explore` is accepted as a compatibility alias for the
canonical `explorer` child profile.

`disabled_tools` is a global mapping keyed by exact tool ID. A `true` value
disables that tool for every profile, even when the profile lists it in
`allowed_tools`. A `false` value re-enables an entry set to `true` by a
lower-precedence configuration layer. Only entries whose effective value is
`true` are disabled.

```yaml
disabled_tools:
  web_fetch: true
```

CLI executable discovery is configured with ordered `cli_utilities.expected`
and `cli_utilities.optional` sequences. User sequences replace their respective
predefined defaults. Names must be nonempty executable basenames without
whitespace or path separators and must be unique within each sequence. A name
may occur in both sequences; in that case the expected classification wins.
Both keys are required in the effective merged configuration.

```yaml
cli_utilities:
  expected:
    - git
    - rg
  optional:
    - docker
    - dotnet
```

Sandbox configuration is not the complete filesystem boundary. Every effective
security profile includes mandatory protection for Parrot's state,
configuration, and data roots. Configured profile rules, workspace nesting,
symlinks, and user-approved write grants cannot bypass that protection. Parrot
may add a trusted, session-scoped runtime capability for a narrow private
artifact subtree, such as the plan directory, without exposing the containing
private root. Filesystem access does not grant network access.

The plan foreground profile receives its private plan-artifact location and
runtime-only write permission from Parrot; child profiles never inherit this
capability. Legacy `profiles.<id>.status` input is accepted and ignored for
compatibility. It is not profile guidance and is never injected into a prompt.

## Write permission requests

`request_write_permission` is the only way an agent can ask the user to extend
its sandbox write access at runtime. A request contains one or more exact,
existing absolute paths and a nonblank reason. The server resolves each path to
its canonical physical target: an existing-file grant applies only to that file,
while an existing-directory grant applies to that directory and its descendants.
A request does not authorise a tool name, a profile, or an unbounded part of the
filesystem.

The CLI offers only the server-declared choices: **Grant**, **Reject**, and
**Reject with reason**. Cancelling the picker or reason entry is a plain reject;
a blank required rejection reason is invalid. Noninteractive sessions reject
requests immediately, and unanswered pending requests time out. A rejection
leaves the sandbox unchanged.

An approval creates a runtime-only grant for the requesting agent session. It
allows writing through the filesystem tools, including write, edit, and shell,
within the granted target; it is not written to configuration, does not survive
the session, and is not inherited by child or sibling agent sessions. Grants
are not merged into a `SecurityProfile`, and cannot override a read-only
profile, an explicit static deny, or Parrot's protected state, configuration,
and data roots. Filesystem permission still does not imply network permission.

## User Session Isolation

A user session is Parrot's unit of private storage, runtime ownership, and child
agent containment. It has one private root beneath Parrot's state directory for
its database, queues, plans, process-output blobs, and internal artifacts. Those
artifacts are never placed in the workspace. At most one active runtime owns a
user session and writes its database; child agents remain within that same user
session and cannot acquire another session's state.

The workspace is the exact working directory from which the session was
launched. Parrot retains that path for execution and agent context. It uses a
canonical or physical identity only to compare and claim workspaces; canonical
resolution does not silently replace the launch path. A workspace is shared
context and is not a place for Parrot's private artifacts.

Session admission has three intentionally different forms:

- **Fresh create** always creates a new user session with root agent `main`.
- **Exact resume** opens only the requested existing session and fails if it
  cannot safely acquire that session.
- **Default open** creates when there is no matching session, resumes when
  exactly one matching session can be selected, and rejects ambiguity rather
  than picking one arbitrarily.

A live owner is never joined or displaced. Resuming an older session preserves
its stored root-agent name instead of rewriting compatibility data. Session
listing is a server-authoritative management operation when connected to a
server; management callers read metadata through `SessionCatalog` and never
obtain a live session, database, queue, or repository.

## Service Transport

Local mode uses the in-process gRPC contract and binds no socket. When Parrot is
hosted, its default control transport is a Unix-domain socket in a user-only
control directory. The directory is mode `0700` and the socket is mode `0600`,
so another local user cannot connect through filesystem access.

TCP serving is explicit and authenticated; there is no unauthenticated TCP
fallback. Plaintext TCP beyond loopback additionally requires an explicit unsafe
acknowledgement and emits a warning. It is intended only for a deployment where
a secure proxy supplies transport protection. Authentication controls admission
to the service but does not weaken the per-user-session ownership checks.

## Model Aliases

Model aliases give stable names to model selectors. The effective configuration
combines these four predefined aliases with the `model_aliases` map in the
configuration file: `low_llm`, `medium_llm`, `high_llm`, and `xhigh_llm`.
Their predefined `usage` values are:

- `low_llm`: `mechanical, single file task, text or code processing when no suitable cli tool available.`
- `medium_llm`: `Decently capable, specific clear task, component level task, two or three file window`
- `high_llm`: `General purpose, agent spawner, tactical decision making and planning, debugging, colaborator`
- `xhigh_llm`: `Strategic work spanning multiple modules or parties, ambiguous or open-ended requirements, hard debugging or optimization, and high-level planning where cheaper models are insufficient.`

The ordinary predefined `model_aliases` targets remain empty. Consequently each
unconfigured alias produces this startup warning until it is configured:
`warning: model alias "NAME" is not configured. Use /model-alias to configure.`

Provider defaults are a separate, opt-in complete set of targets. They are
configured under `provider_model_alias_defaults`; every provider entry must map
**all four** predefined aliases (`low_llm`, `medium_llm`, `high_llm`, and
`xhigh_llm`). A partial provider mapping is invalid. The shipped ChatGPT
mapping is:

```yaml
provider_model_alias_defaults:
  chatgpt:
    low_llm: chatgpt/gpt-5.6-terra/medium
    medium_llm: chatgpt/gpt-5.6-sol/medium
    high_llm: chatgpt/gpt-5.6-sol/high
    xhigh_llm: chatgpt/gpt-5.6-sol/xhigh
```

A configured entry can override any predefined field without losing its default
metadata, and can add another alias. Provider defaults do not implicitly set
ordinary aliases: they are applied only when chosen through `/model-alias`.

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

A spawned child runs independently and `agent_spawn` returns its session ID
immediately. Friendly child names are unique only among one agent's direct
children, while canonical agent session IDs remain usable throughout the
enclosing user session. `agent_send` checks exact canonical spawned-agent IDs
first. For a sender with a registered direct parent, the case-sensitive literal
`parent`, actual parent ID, or actual parent friendly name resolves second and
takes precedence over a colliding direct-child friendly name; direct-child names
resolve last. For a root sender, `parent` has no special meaning and can resolve
a direct child with that name. `wait_agent` remains child-only and uses unchanged
child resolution: a canonical child ID or direct-child friendly name.
When each child execution finishes, Parrot automatically sends its terminal
status and result to its direct parent as normal steering input. A parent can use
`wait_agent` when it needs to block for the retained child result instead.

The generic `wait` tool pauses for incoming activity and returns early for a new
message, direct-child completion, unclaimed yielded-process completion, or an
item in a queue the agent enabled with `queue_listen`. A successful wake result
remains short. Timeout output inventories all queues, active processes, and
active subagents. The queue inventory includes every queue regardless of
`queue_listen`; listening controls only whether queue activity is eligible to
wake the wait. This activity wait is distinct from the specialized `wait_agent`,
which reads a retained direct-child result, and `wait_process`, which waits for
one named process.

When `exec_command` yields, its result carries a typed yielded-process handoff
rather than requiring clients to recognize text. The service also publishes
complete snapshots of the user session's currently active shell processes from
its authoritative in-memory owners. Enhanced clients replace their local process
inventory with each snapshot, including the initial snapshot after reconnect,
so a dropped stream cannot leave stale rows or lose surviving processes. A
process row remains live after the originating tool call yields and is removed
only when a later snapshot reports that the process has actually exited.

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

Its alias picker also has a **Use provider defaults** entry, described as
**Configure all four model aliases**. Selecting it opens **Select provider
defaults**. That picker lists only provider IDs that both have a complete
four-alias mapping and are currently available through the executing server's
`ListModels` result. Thus a configured provider-default mapping is not offered
merely because it exists in configuration; the server must currently make that
provider available. If none qualify, the command reports `no available
providers have model alias defaults`.

Choosing a provider-default entry is server-authoritative: the server validates
and writes all four ordinary alias targets as one atomic operation, so it never
leaves a partially applied provider set. On success it reports `Model aliases
configured from PROVIDER defaults`. A remote CLI queries and updates that
authoritative server configuration; it does not modify its own local
configuration instead.

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
command. In the enhanced main chat input, Shift+Enter inserts a newline. Enter
submits after 100ms of quiet; typing or editing during that grace period instead
inserts a newline, allowing fast unbracketed multiline messages. The basic CLI
remains line-based.

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

Linux build and publish output also contains `parrot-pty-attach` beside
`parrot`. The CLI resolves this private pseudo-terminal helper from its own
application directory; Nix additionally installs it in `bin` so the wrapped
CLI and the helper are available from the same package.

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

## Static musl

The shipped binary is linked statically against musl. It has no interpreter and
no `NEEDED` entry, so there is no dynamic loader or shared-library dependency to
install. It runs without relying on the host system's libc.

```console
$ ldd parrot
        statically linked
```

It costs about 1.4 MB over a glibc-dynamic build (5.8 MB against 4.4 MB), and
release symbols are retained so a core dump from a shipped binary remains
usable.

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
part of the build. The analyzer configuration is recorded in
[`.editorconfig`](.editorconfig).

## For Contributors

Read [AGENTS.md](AGENTS.md) before changing code. It documents the development
guidelines, supported environment, required gates, and repository conventions.
