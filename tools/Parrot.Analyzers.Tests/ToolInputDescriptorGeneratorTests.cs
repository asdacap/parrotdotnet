using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Parrot.Analyzers.Tests;

internal sealed class ToolInputDescriptorGeneratorTests
{
    private const string Preamble = """
        using System;
        using System.Collections.Generic;
        using System.ComponentModel;
        using System.Text.Json.Serialization;

        namespace Parrot.Tools.Schema
        {
            internal enum AdditionalPropertiesPolicy
            {
                Reject = 0,
                Allow = 1,
            }

            [AttributeUsage(AttributeTargets.Class)]
            internal sealed class ToolInputModelAttribute : Attribute
            {
                public ToolInputModelAttribute(AdditionalPropertiesPolicy policy)
                {
                }
            }
        }

        namespace Models
        {
        """;

    [Test]
    public async Task A_top_level_model_still_compiles_and_generates_its_schema()
    {
        var (generatorDiagnostics, compilationDiagnostics, generatedSources) = RunGeneration("""
            [Parrot.Tools.Schema.ToolInputModel(Parrot.Tools.Schema.AdditionalPropertiesPolicy.Reject)]
            internal sealed partial class Input
            {
                [Description("A value")]
                public string Value { get; set; } = string.Empty;
            }
            """);

        _ = await Assert.That(generatorDiagnostics).Count().IsEqualTo(0);
        _ = await Assert.That(string.Join(
            Environment.NewLine,
            compilationDiagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))).IsEqualTo(string.Empty);
        _ = await Assert.That(generatedSources).Count().IsEqualTo(1);
        _ = await Assert.That(generatedSources[0].Value).Contains("internal sealed partial class Input");
        _ = await Assert.That(ExtractDescriptor(generatedSources[0].Value)).IsEqualTo("{\"type\":\"object\",\"properties\":{\"Value\":{\"type\":\"string\",\"description\":\"A value\"}},\"additionalProperties\":false}");
    }

    [Test]
    public async Task A_model_nested_through_partial_types_compiles_and_generates_its_schema()
    {
        var (generatorDiagnostics, compilationDiagnostics, generatedSources) = RunGeneration("""
            public abstract partial record class Outer
            {
                internal readonly partial record struct Middle
                {
                    [Parrot.Tools.Schema.ToolInputModel(Parrot.Tools.Schema.AdditionalPropertiesPolicy.Reject)]
                    private sealed partial record class Input
                    {
                        [Description("A value")]
                        public string Value { get; set; } = string.Empty;
                    }
                }
            }
            """);

        _ = await Assert.That(generatorDiagnostics).Count().IsEqualTo(0);
        _ = await Assert.That(string.Join(
            Environment.NewLine,
            compilationDiagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))).IsEqualTo(string.Empty);
        _ = await Assert.That(generatedSources).Count().IsEqualTo(1);
        _ = await Assert.That(generatedSources[0].Value).Contains("public abstract partial record class Outer");
        _ = await Assert.That(generatedSources[0].Value).Contains("internal readonly partial record struct Middle");
        _ = await Assert.That(generatedSources[0].Value).Contains("private sealed partial record class Input");
        _ = await Assert.That(generatedSources[0].Value).Contains("private static string Descriptor");
        _ = await Assert.That(ExtractDescriptor(generatedSources[0].Value)).IsEqualTo("{\"type\":\"object\",\"properties\":{\"Value\":{\"type\":\"string\",\"description\":\"A value\"}},\"additionalProperties\":false}");
    }

    [Test]
    public async Task Nested_input_models_generate_all_descriptors_and_the_input_graph_schema()
    {
        var (generatorDiagnostics, compilationDiagnostics, generatedSources) = RunGeneration("""
            internal sealed partial class Tool
            {
                [Parrot.Tools.Schema.ToolInputModel(Parrot.Tools.Schema.AdditionalPropertiesPolicy.Reject)]
                internal sealed partial class Input
                {
                    [Description("Questions to ask")]
                    public Question[]? Questions { get; set; }

                    [Parrot.Tools.Schema.ToolInputModel(Parrot.Tools.Schema.AdditionalPropertiesPolicy.Reject)]
                    internal sealed partial class Question
                    {
                        [Description("Available options")]
                        public Option[]? Options { get; set; }
                    }

                    [Parrot.Tools.Schema.ToolInputModel(Parrot.Tools.Schema.AdditionalPropertiesPolicy.Reject)]
                    internal sealed partial class Option
                    {
                        [Description("Option label")]
                        public string Label { get; set; } = string.Empty;
                    }
                }
            }
            """);

        _ = await Assert.That(generatorDiagnostics).Count().IsEqualTo(0);
        _ = await Assert.That(string.Join(
            Environment.NewLine,
            compilationDiagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))).IsEqualTo(string.Empty);
        _ = await Assert.That(generatedSources).Count().IsEqualTo(3);
        var inputSource = generatedSources.Single(source =>
            source.Key.EndsWith("_Input.Descriptor.g.cs", StringComparison.Ordinal));
        _ = await Assert.That(ExtractDescriptor(inputSource.Value)).IsEqualTo("{\"type\":\"object\",\"properties\":{\"Questions\":{\"type\":\"array\",\"items\":{\"type\":\"object\",\"properties\":{\"Options\":{\"type\":\"array\",\"items\":{\"type\":\"object\",\"properties\":{\"Label\":{\"type\":\"string\",\"description\":\"Option label\"}},\"additionalProperties\":false},\"description\":\"Available options\"}},\"additionalProperties\":false},\"description\":\"Questions to ask\"}},\"additionalProperties\":false}");
    }

    [Test]
    public async Task A_generic_model_reports_PARROT1002()
    {
        var diagnostics = Generate("""
            [Parrot.Tools.Schema.ToolInputModel(Parrot.Tools.Schema.AdditionalPropertiesPolicy.Reject)]
            internal sealed partial class Input<T>
            {
            }
            """);

        await AssertSingleDiagnostic(
            diagnostics,
            "PARROT1002",
            "Tool input model 'Models.Input<T>' must be non-generic and nested only in non-generic partial types");
    }

    [Test]
    public async Task A_model_in_a_generic_containing_type_reports_PARROT1002()
    {
        var diagnostics = Generate("""
            internal partial class Outer<T>
            {
                [Parrot.Tools.Schema.ToolInputModel(Parrot.Tools.Schema.AdditionalPropertiesPolicy.Reject)]
                internal sealed partial class Input
                {
                }
            }
            """);

        await AssertSingleDiagnostic(
            diagnostics,
            "PARROT1002",
            "Tool input model 'Models.Outer<T>.Input' must be non-generic and nested only in non-generic partial types");
    }

    [Test]
    public async Task A_model_in_a_non_partial_containing_type_reports_PARROT1002()
    {
        var diagnostics = Generate("""
            internal class Outer
            {
                [Parrot.Tools.Schema.ToolInputModel(Parrot.Tools.Schema.AdditionalPropertiesPolicy.Reject)]
                internal sealed partial class Input
                {
                }
            }
            """);

        await AssertSingleDiagnostic(
            diagnostics,
            "PARROT1002",
            "Tool input model 'Models.Outer.Input' must be non-generic and nested only in non-generic partial types");
    }

    [Test]
    public async Task A_property_without_a_description_reports_PARROT1009()
    {
        var diagnostics = Generate("""
            [Parrot.Tools.Schema.ToolInputModel(Parrot.Tools.Schema.AdditionalPropertiesPolicy.Reject)]
            internal sealed partial class Input
            {
                public string Value { get; set; } = string.Empty;
            }
            """);

        await AssertSingleDiagnostic(diagnostics, "PARROT1009", "Property 'Value' must have a non-empty DescriptionAttribute");
    }

    [Test]
    public async Task A_property_with_an_empty_description_reports_PARROT1009()
    {
        var diagnostics = Generate("""
            [Parrot.Tools.Schema.ToolInputModel(Parrot.Tools.Schema.AdditionalPropertiesPolicy.Reject)]
            internal sealed partial class Input
            {
                [Description("")]
                public string Value { get; set; } = string.Empty;
            }
            """);

        await AssertSingleDiagnostic(diagnostics, "PARROT1009", "Property 'Value' must have a non-empty DescriptionAttribute");
    }

    [Test]
    [Arguments("[JsonIgnore]", "JsonIgnore properties are not supported")]
    [Arguments("[JsonExtensionData]", "JsonExtensionData properties are not supported")]
    public async Task STJ_exclusion_properties_report_PARROT1010(string attribute, string reason)
    {
        var type = attribute == "[JsonExtensionData]" ? "Dictionary<string, object>" : "string";
        var initializer = attribute == "[JsonExtensionData]" ? "new()" : "string.Empty";
        var diagnostics = Generate($$"""
            [Parrot.Tools.Schema.ToolInputModel(Parrot.Tools.Schema.AdditionalPropertiesPolicy.Reject)]
            internal sealed partial class Input
            {
                {{attribute}}
                [Description("A value")]
                public {{type}} Value { get; set; } = {{initializer}};
            }
            """);

        await AssertSingleDiagnostic(diagnostics, "PARROT1010", $"Tool input model member 'Value' is not supported: {reason}");
    }

    [Test]
    public async Task A_getter_only_property_reports_PARROT1010()
    {
        var diagnostics = Generate("""
            [Parrot.Tools.Schema.ToolInputModel(Parrot.Tools.Schema.AdditionalPropertiesPolicy.Reject)]
            internal sealed partial class Input
            {
                [Description("A value")]
                public string Value { get; } = string.Empty;
            }
            """);

        await AssertSingleDiagnostic(
            diagnostics,
            "PARROT1010",
            "Tool input model member 'Value' is not supported: properties must have public getters and setters");
    }

    [Test]
    public async Task An_inherited_model_reports_PARROT1010()
    {
        var diagnostics = Generate("""
            internal class BaseInput
            {
            }

            [Parrot.Tools.Schema.ToolInputModel(Parrot.Tools.Schema.AdditionalPropertiesPolicy.Reject)]
            internal sealed partial class Input : BaseInput
            {
            }
            """);

        await AssertSingleDiagnostic(
            diagnostics,
            "PARROT1010",
            "Tool input model member 'Models.Input' is not supported: inheritance is not supported");
    }

    private static ImmutableArray<Diagnostic> Generate(string model) =>
        RunGeneration(model).GeneratorDiagnostics;

    private static (
        ImmutableArray<Diagnostic> GeneratorDiagnostics,
        ImmutableArray<Diagnostic> CompilationDiagnostics,
        ImmutableArray<KeyValuePair<string, string>> GeneratedSources) RunGeneration(string model)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(Preamble + model + "\n}");
        var trustedAssemblies = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string
            ?? throw new InvalidOperationException("Trusted platform assemblies are unavailable.");
        var references = trustedAssemblies.Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create(
            "GeneratorTest",
            [syntaxTree],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var driver = CSharpGeneratorDriver.Create(new ToolInputDescriptorGenerator().AsSourceGenerator());

        var resultDriver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out _);
        var runResult = resultDriver.GetRunResult();
        var generatorDiagnostics = runResult.Results.SelectMany(result => result.Diagnostics).ToImmutableArray();
        var generatedSources = runResult.Results
            .SelectMany(result => result.GeneratedSources)
            .Select(source => new KeyValuePair<string, string>(source.HintName, source.SourceText.ToString()))
            .ToImmutableArray();
        return (generatorDiagnostics, outputCompilation.GetDiagnostics(), generatedSources);
    }

    private static string ExtractDescriptor(string generatedSource)
    {
        var property = CSharpSyntaxTree.ParseText(generatedSource)
            .GetRoot()
            .DescendantNodes()
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.PropertyDeclarationSyntax>()
            .Single(declaration => declaration.Identifier.ValueText == "Descriptor");
        return property.ExpressionBody?.Expression is Microsoft.CodeAnalysis.CSharp.Syntax.LiteralExpressionSyntax literal
            ? literal.Token.ValueText
            : throw new InvalidOperationException("Generated Descriptor is not a string literal expression.");
    }

    private static async Task AssertSingleDiagnostic(
        ImmutableArray<Diagnostic> diagnostics,
        string id,
        string message)
    {
        _ = await Assert.That(diagnostics).Count().IsEqualTo(1);
        _ = await Assert.That(diagnostics[0].Id).IsEqualTo(id);
        _ = await Assert.That(diagnostics[0].GetMessage()).IsEqualTo(message);
    }
}
