using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Parrot.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class NoEventsOrDelegatesAnalyzer : DiagnosticAnalyzer
{
    public const string EventRuleId = "PARROT0001";
    public const string DelegateRuleId = "PARROT0002";

    private const string Category = "Parrot.Design";

    private const string EventDescription =
        "A multicast delegate gives no delivery ordering across subscribers and no backpressure, "
        + "which the event-stream ordering guarantee depends on. See AGENTS.md and MIGRATION.md section 5.";

    private const string DelegateDescription =
        "A named delegate type is a one-method interface that cannot be extended. "
        + "See AGENTS.md and MIGRATION.md section 5.";

    private static readonly DiagnosticDescriptor EventRule = new(
        EventRuleId,
        "Do not declare an event",
        "'{0}' is an event; publish through Channel<T> or IAsyncEnumerable<T> instead",
        Category,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: EventDescription);

    private static readonly DiagnosticDescriptor DelegateRule = new(
        DelegateRuleId,
        "Do not declare a delegate type",
        "'{0}' is a delegate type; use an interface, or Func<>/Action<> for a local callback",
        Category,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: DelegateDescription);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        [EventRule, DelegateRule];

    public override void Initialize(AnalysisContext context)
    {
        if (context is null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(ReportEventField, SyntaxKind.EventFieldDeclaration);
        context.RegisterSyntaxNodeAction(ReportEventProperty, SyntaxKind.EventDeclaration);
        context.RegisterSyntaxNodeAction(ReportDelegate, SyntaxKind.DelegateDeclaration);
    }

    private static void ReportEventField(SyntaxNodeAnalysisContext context)
    {
        var declaration = (EventFieldDeclarationSyntax)context.Node;

        foreach (var declarator in declaration.Declaration.Variables)
        {
            Report(context, EventRule, declarator.Identifier);
        }
    }

    private static void ReportEventProperty(SyntaxNodeAnalysisContext context) =>
        Report(context, EventRule, ((EventDeclarationSyntax)context.Node).Identifier);

    private static void ReportDelegate(SyntaxNodeAnalysisContext context) =>
        Report(context, DelegateRule, ((DelegateDeclarationSyntax)context.Node).Identifier);

    private static void Report(SyntaxNodeAnalysisContext context, DiagnosticDescriptor rule, SyntaxToken identifier) =>
        context.ReportDiagnostic(Diagnostic.Create(rule, identifier.GetLocation(), identifier.ValueText));
}
