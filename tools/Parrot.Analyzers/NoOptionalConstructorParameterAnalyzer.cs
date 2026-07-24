using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Parrot.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class NoOptionalConstructorParameterAnalyzer : DiagnosticAnalyzer
{
    public const string RuleId = "PARROT0004";

    private const string Description =
        "A defaulted constructor parameter hides a dependency and creates two configurations of the "
        + "same type, where tests take one path and production takes the other. Pass it explicitly, "
        + "or if the absent case is real, give it a type that says so. See docs/style.md.";

    private static readonly DiagnosticDescriptor Rule = new(
        RuleId,
        "Do not give a constructor parameter a default value",
        "'{0}' has a default value; a constructor takes what the object needs, explicitly",
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
        context.RegisterSyntaxNodeAction(ReportDeclared, SyntaxKind.ConstructorDeclaration);
        context.RegisterSyntaxNodeAction(
            ReportPrimary,
            SyntaxKind.ClassDeclaration,
            SyntaxKind.RecordDeclaration,
            SyntaxKind.StructDeclaration,
            SyntaxKind.RecordStructDeclaration);
    }

    private static void ReportDeclared(SyntaxNodeAnalysisContext context) =>
        Report(context, ((ConstructorDeclarationSyntax)context.Node).ParameterList);

    // A primary constructor is still a constructor.
    private static void ReportPrimary(SyntaxNodeAnalysisContext context) =>
        Report(context, ((TypeDeclarationSyntax)context.Node).ParameterList);

    private static void Report(SyntaxNodeAnalysisContext context, BaseParameterListSyntax? parameters)
    {
        if (parameters is null)
        {
            return;
        }

        foreach (var parameter in parameters.Parameters)
        {
            if (parameter.Default is not null)
            {
                context.ReportDiagnostic(
                    Diagnostic.Create(Rule, parameter.GetLocation(), parameter.Identifier.ValueText));
            }
        }
    }
}
