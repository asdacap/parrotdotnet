using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Parrot.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class NoNullForgivingOperatorAnalyzer : DiagnosticAnalyzer
{
    public const string RuleId = "PARROT0003";

    private const string Description =
        "The null-forgiving operator asserts what the compiler could not prove, and nothing "
        + "re-checks it later. Narrow the type, check the value, or fix the API that returns a "
        + "nullable it never nulls. See docs/style.md.";

    private static readonly DiagnosticDescriptor Rule = new(
        RuleId,
        "Do not suppress a nullable warning with '!'",
        "'!' suppresses a nullable warning here; prove the value instead of asserting it",
        "Parrot.Design",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: Description);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        if (context is null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Report, SyntaxKind.SuppressNullableWarningExpression);
    }

    private static void Report(SyntaxNodeAnalysisContext context) =>
        context.ReportDiagnostic(Diagnostic.Create(Rule, context.Node.GetLocation()));
}
