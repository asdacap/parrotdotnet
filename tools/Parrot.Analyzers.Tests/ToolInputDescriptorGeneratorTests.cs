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

    private static ImmutableArray<Diagnostic> Generate(string model)
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

        var resultDriver = driver.RunGeneratorsAndUpdateCompilation(compilation, out _, out _);
        return [.. resultDriver.GetRunResult().Results.SelectMany(result => result.Diagnostics)];
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
