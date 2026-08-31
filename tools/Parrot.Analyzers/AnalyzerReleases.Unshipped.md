; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
PARROT0001 | Parrot.Design | Warning | Do not declare an event. AGENTS.md: no dotnet events.
PARROT0002 | Parrot.Design | Warning | Do not declare a delegate type. AGENTS.md: no dotnet delegates.
PARROT0003 | Parrot.Design | Warning | Do not suppress a nullable warning with '!'. Strict nullable.
PARROT0004 | Parrot.Design | Warning | Do not give a parameter a default value.
