# Style And Analysis

Every rule below is enforced at build time. `TreatWarningsAsErrors` is on for
every project, so there is no state in which a violation is merely reported.

## What is turned on

| Layer | Configured in | What it covers |
| --- | --- | --- |
| Compiler | `Directory.Build.props` | `WarningLevel 9999`, nullable reference types, deterministic builds |
| .NET analyzers | `AnalysisLevel latest-all` | Every `CA` rule ships enabled, including the ones normally opt-in |
| IDE code style | `EnforceCodeStyleInBuild` + `.editorconfig` | `IDE` rules — formatting, `var` usage, expression bodies, unused code |
| StyleCop | `StyleCop.Analyzers` package | `SA` rules — member ordering, spacing, documentation phrasing |
| Trim / AOT | `EnableTrimAnalyzer`, `EnableAotAnalyzer` | `IL2xxx` and `IL3xxx`, as errors, in every project |
| Parrot's own | `tools/Parrot.Analyzers` | `PARROT0001` no events, `PARROT0002` no delegate types |

`IsAotCompatible` is set on every source project, not only on `Parrot.Cli`, so
an AOT violation surfaces in the assembly that introduced it rather than at the
final link.

## Decisions worth knowing before you write code

- **`var` everywhere, without exception.** All three `csharp_style_var_*`
  options are `true:error`, including for built-in types. A local's type is the
  compiler's business. If you cannot tell what a local holds, the fix is a
  longer, unambiguous name — `inner` is not a name — not a type annotation.
- **File-scoped namespaces, braces always, usings outside the namespace.**
  All three are errors.
- **Primary constructors** where applicable — `error`, not a suggestion.
- **Expression-bodied members** where they fit on one line — including methods
  that return `void` or only throw.
- **Private fields are `_camelCase`;** everything else including constants and
  static readonly fields is `PascalCase`. Interfaces are `I`-prefixed, type
  parameters `T`-prefixed. All naming rules are errors.
- **`IFormatProvider` and `StringComparison` are mandatory** (CA1305, CA1307,
  CA1309, CA1310 as errors). `InvariantGlobalization` is on, so a culture-
  sensitive comparison that slips through behaves differently in the published
  binary than in a test host.
- **`CancellationToken` must be forwarded** (CA2016 as an error).
- **Unused code fails the build** — unused usings, private members, parameters,
  and assignments (IDE0005, IDE0051, IDE0052, IDE0059, IDE0060). Dead code is a
  migration hazard: it looks ported but is never exercised.
- **One top-level symbol per file, named after the file.** SA1402 (one type),
  SA1403 (one namespace), SA1649 (file name matches the type), all errors.
  `stylecop.json` widens SA1402 from its default of `class` to every type kind,
  so an `enum` or `delegate` smuggled in beside a class is caught too.
- **No `event`s and no `delegate` types** (PARROT0001, PARROT0002). See below.
- **Strict nullable.** `Nullable` is `enable` everywhere, its warnings are
  errors, and `PARROT0003` removes the `!` escape hatch. `!` asserts what the
  compiler could not prove and nothing re-checks it later; narrow the type,
  check the value, or fix the API that declares a nullable it never nulls.
  `CA1062` (null-check public arguments) is off because the type system does it.
- Line length is 120.

## Parrot's own analyzer

`AGENTS.md` bans .NET events and delegates. No shipped analyzer expresses that —
StyleCop, the .NET analyzers, and `BannedApiAnalyzers` all govern how an API is
*used*, not whether a language construct may be *declared* — so
`tools/Parrot.Analyzers` provides it.

| Rule | Catches |
| --- | --- |
| `PARROT0001` | Field-like `event` declarations and `event` properties with `add`/`remove` |
| `PARROT0002` | `delegate` type declarations |
| `PARROT0003` | The null-forgiving operator, `x!` |

`Func<>` and `Action<>` as parameters are still permitted: the rule targets a
named delegate *type*, which is a one-method interface that cannot be extended.
An interface is the extension seam; a local callback is not.

`CA1030` ("consider making this an event") is turned off because it asks for the
opposite of `PARROT0001`.

The project is referenced by every other project through `Directory.Build.props`
with `OutputItemType="Analyzer"`, so it never reaches the runtime or the AOT
link. It targets `netstandard2.0` because it runs inside the compiler. It is
held to the same style rules as everything else — writing it took two rounds of
fixing its own lint failures.

To add a rule: add a descriptor and a `RegisterSyntaxNodeAction`, list the rule
in `AnalyzerReleases.Unshipped.md` (`RS2008` fails the build otherwise), and set
its severity in `.editorconfig`.

## Wrapping

Wrapping after `=>`, after `:` in a constructor initialiser, and inside a
conditional expression is permitted. The corresponding `allow_blank_line_after_*`
experimental options are deliberately left at their permissive default:
forbidding them pushes long expressions past the line limit, which is the worse
readability trade.

## Test projects

`Directory.Build.targets` relaxes exactly five rules when `IsTestProject` is
true, each with a comment giving the reason: `CA1515` (a runner needs public
classes), `CA1707` (underscored test names), `CA1861` (constant arrays as test
data), `CA2007` (no synchronisation context), and `IDE0058` (fluent assertion
chains return a value nobody consumes). Nothing else is relaxed. Test code is
held to the same standard as `src`.

## Adding an exception

In order of preference:

1. **Change the code.** In almost every case the analyzer is right and the fix
   is smaller than the argument.
2. **Turn the rule off repository-wide** in `.editorconfig`, in the appropriate
   section, with a comment stating why. This is the right move when the rule
   targets redistributable-library design and Parrot is an application — see the
   existing `CA1002` / `CA1034` / `CA1062` block.
3. **Turn it off for test projects only**, in `Directory.Build.targets`.

Inline `#pragma warning disable` and `[SuppressMessage]` are not permitted. A
suppression that is invisible in configuration is a suppression nobody reviews.

`IL2xxx` and `IL3xxx` are never suppressible by any of these routes. An AOT
violation is a design problem; see MIGRATION.md §2.

## Formatting

```sh
dotnet format Parrot.slnx                    # fix
dotnet format Parrot.slnx --verify-no-changes # gate
```

Nix files are formatted with `alejandra`; `nix flake check` verifies it.
