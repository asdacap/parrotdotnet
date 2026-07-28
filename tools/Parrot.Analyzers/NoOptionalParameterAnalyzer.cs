using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Parrot.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class NoOptionalParameterAnalyzer : DiagnosticAnalyzer
{
    public const string RuleId = "PARROT0004";

    private const string Description =
        "A defaulted parameter hides a caller decision. Pass every value explicitly, or if the absent "
        + "case is real, give it a type that says so. See AGENTS.md.";

    private static readonly DiagnosticDescriptor Rule = new(
        RuleId,
        "Do not give a parameter a default value",
        "'{0}' has a default value; callers must pass it explicitly",
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
        context.RegisterSyntaxNodeAction(Report, SyntaxKind.Parameter);
    }

    private static void Report(SyntaxNodeAnalysisContext context)
    {
        var parameter = (ParameterSyntax)context.Node;

        if (parameter.Default is not null)
        {
            context.ReportDiagnostic(
                Diagnostic.Create(Rule, parameter.GetLocation(), parameter.Identifier.ValueText));
        }
    }
}
