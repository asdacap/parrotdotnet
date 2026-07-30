; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
PARROT0001 | Parrot.Design | Warning | Do not declare an event. AGENTS.md: no dotnet events.
PARROT0002 | Parrot.Design | Warning | Do not declare a delegate type. AGENTS.md: no dotnet delegates.
PARROT0003 | Parrot.Design | Warning | Do not suppress a nullable warning with '!'. Strict nullable.
PARROT0004 | Parrot.Design | Warning | Do not give a parameter a default value.
PARROT1001 | Parrot.Generation | Error | Tool input model must be partial.
PARROT1002 | Parrot.Generation | Error | Tool input model must be top-level and non-generic.
PARROT1003 | Parrot.Generation | Error | Tool input property type is unsupported.
PARROT1004 | Parrot.Generation | Error | Nested tool input model must be marked.
PARROT1005 | Parrot.Generation | Error | Tool input constraint is invalid.
PARROT1006 | Parrot.Generation | Error | Additional-properties policy is invalid.
PARROT1007 | Parrot.Generation | Error | Tool input property name is duplicated.
PARROT1008 | Parrot.Generation | Error | Tool input model graph is recursive.
PARROT1009 | Parrot.Generation | Error | Tool input property requires a non-empty description.
PARROT1010 | Parrot.Generation | Error | Tool input model shape is unsupported by descriptor generation.
