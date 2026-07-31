using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Parrot.Analyzers;

[Generator(LanguageNames.CSharp)]
public sealed class ToolInputDescriptorGenerator : IIncrementalGenerator
{
    private const string MarkerAttributeName = "Parrot.Tools.Schema.ToolInputModelAttribute";
    private const string RequiredAttributeName = "Parrot.Tools.Schema.ToolRequiredAttribute";
    private const string MinLengthAttributeName = "Parrot.Tools.Schema.ToolMinLengthAttribute";
    private const string MinimumAttributeName = "Parrot.Tools.Schema.ToolMinimumAttribute";
    private const string MaximumAttributeName = "Parrot.Tools.Schema.ToolMaximumAttribute";
    private const string PatternAttributeName = "Parrot.Tools.Schema.ToolPatternAttribute";
    private const string MinItemsAttributeName = "Parrot.Tools.Schema.ToolMinItemsAttribute";
    private const string MaxItemsAttributeName = "Parrot.Tools.Schema.ToolMaxItemsAttribute";
    private const string DefaultBoolAttributeName = "Parrot.Tools.Schema.ToolDefaultBoolAttribute";
    private const string DefaultLongAttributeName = "Parrot.Tools.Schema.ToolDefaultLongAttribute";
    private const string DefaultStringAttributeName = "Parrot.Tools.Schema.ToolDefaultStringAttribute";
    private const string StringEnumAttributeName = "Parrot.Tools.Schema.ToolStringEnumAttribute";
    private const string JsonPropertyNameAttributeName = "System.Text.Json.Serialization.JsonPropertyNameAttribute";
    private const string JsonIgnoreAttributeName = "System.Text.Json.Serialization.JsonIgnoreAttribute";
    private const string JsonExtensionDataAttributeName = "System.Text.Json.Serialization.JsonExtensionDataAttribute";
    private const string JsonIncludeAttributeName = "System.Text.Json.Serialization.JsonIncludeAttribute";
    private const string DescriptionAttributeName = "System.ComponentModel.DescriptionAttribute";

    private static readonly DiagnosticDescriptor PartialModelRule = new(
        "PARROT1001",
        "Tool input model must be partial",
        "Tool input model '{0}' must be partial so its Descriptor can be generated",
        "Parrot.Generation",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor SupportedNestingRule = new(
        "PARROT1002",
        "Tool input model nesting is unsupported",
        "Tool input model '{0}' must be non-generic and nested only in non-generic partial types",
        "Parrot.Generation",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor UnsupportedTypeRule = new(
        "PARROT1003",
        "Tool input property type is unsupported",
        "Property '{0}' has unsupported tool descriptor type '{1}'",
        "Parrot.Generation",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor UnmarkedModelRule = new(
        "PARROT1004",
        "Nested tool input model must be marked",
        "Property '{0}' uses model '{1}', which must have ToolInputModelAttribute",
        "Parrot.Generation",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor InvalidConstraintRule = new(
        "PARROT1005",
        "Tool input constraint is invalid",
        "Property '{0}' has invalid constraint: {1}",
        "Parrot.Generation",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor InvalidPolicyRule = new(
        "PARROT1006",
        "Additional-properties policy is invalid",
        "Tool input model '{0}' has an invalid AdditionalPropertiesPolicy value",
        "Parrot.Generation",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor DuplicatePropertyNameRule = new(
        "PARROT1007",
        "Tool input property name is duplicated",
        "Tool input model '{0}' has more than one property named '{1}'",
        "Parrot.Generation",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor RecursiveModelRule = new(
        "PARROT1008",
        "Tool input model graph is recursive",
        "Tool input model '{0}' recursively contains itself",
        "Parrot.Generation",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor MissingDescriptionRule = new(
        "PARROT1009",
        "Tool input property requires a description",
        "Property '{0}' must have a non-empty DescriptionAttribute",
        "Parrot.Generation",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor UnsupportedModelShapeRule = new(
        "PARROT1010",
        "Tool input model shape is unsupported",
        "Tool input model member '{0}' is not supported: {1}",
        "Parrot.Generation",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var models = context.SyntaxProvider.ForAttributeWithMetadataName(
            MarkerAttributeName,
            static (node, _) => node is TypeDeclarationSyntax,
            static (attributeContext, _) => (INamedTypeSymbol)attributeContext.TargetSymbol);

        context.RegisterSourceOutput(models, static (productionContext, model) =>
            Generate(productionContext, model));
    }

    private static void Generate(SourceProductionContext context, INamedTypeSymbol model)
    {
        if (!IsPartial(model, context.CancellationToken))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                PartialModelRule,
                model.Locations.FirstOrDefault(),
                model.ToDisplayString()));
            return;
        }

        if (!HasSupportedNesting(model, context.CancellationToken))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                SupportedNestingRule,
                model.Locations.FirstOrDefault(),
                model.ToDisplayString()));
            return;
        }

        var diagnostics = new List<Diagnostic>();
        var path = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        var json = BuildModel(model, path, diagnostics, context.CancellationToken);
        foreach (var diagnostic in diagnostics)
        {
            context.ReportDiagnostic(diagnostic);
        }

        if (json is null)
        {
            return;
        }

        var source = BuildSource(model, json);
        context.AddSource(GetHintName(model), SourceText.From(source, Encoding.UTF8));
    }

    private static bool HasSupportedNesting(INamedTypeSymbol model, CancellationToken cancellationToken)
    {
        for (var type = model; type is not null; type = type.ContainingType)
        {
            if (type.TypeParameters.Length != 0 ||
                (type.ContainingType is not null && !IsPartial(type.ContainingType, cancellationToken)))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsPartial(INamedTypeSymbol type, CancellationToken cancellationToken) =>
        type.DeclaringSyntaxReferences
            .Select(reference => reference.GetSyntax(cancellationToken))
            .OfType<TypeDeclarationSyntax>()
            .Any(declaration => declaration.Modifiers.Any(SyntaxKind.PartialKeyword));

    private static string? BuildModel(
        INamedTypeSymbol model,
        HashSet<INamedTypeSymbol> path,
        List<Diagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        if (!path.Add(model))
        {
            diagnostics.Add(Diagnostic.Create(
                RecursiveModelRule,
                model.Locations.FirstOrDefault(),
                model.ToDisplayString()));
            return null;
        }

        var marker = FindAttribute(model, MarkerAttributeName);
        if (marker is null || marker.ConstructorArguments.Length != 1 ||
            marker.ConstructorArguments[0].Value is not int policy || policy is < 0 or > 1)
        {
            diagnostics.Add(Diagnostic.Create(
                InvalidPolicyRule,
                model.Locations.FirstOrDefault(),
                model.ToDisplayString()));
            _ = path.Remove(model);
            return null;
        }

        if (!ValidateModelShape(model, diagnostics))
        {
            _ = path.Remove(model);
            return null;
        }

        var properties = model.GetMembers()
            .OfType<IPropertySymbol>()
            .Where(property => !property.IsStatic && !property.IsIndexer &&
                               property.DeclaredAccessibility == Accessibility.Public &&
                               SymbolEqualityComparer.Default.Equals(property.ContainingType, model))
            .OrderBy(GetSourcePath, StringComparer.Ordinal)
            .ThenBy(GetSourcePosition)
            .ThenBy(property => property.Name, StringComparer.Ordinal)
            .ToArray();

        var names = new HashSet<string>(StringComparer.Ordinal);
        var propertySchemas = new List<KeyValuePair<string, string>>(properties.Length);
        var required = new List<string>();
        var valid = true;

        foreach (var property in properties)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = GetPropertyName(property, diagnostics);
            if (name is null)
            {
                valid = false;
                continue;
            }

            if (!names.Add(name))
            {
                diagnostics.Add(Diagnostic.Create(
                    DuplicatePropertyNameRule,
                    property.Locations.FirstOrDefault(),
                    model.ToDisplayString(),
                    name));
                valid = false;
                continue;
            }

            if (!HasDescription(property))
            {
                diagnostics.Add(Diagnostic.Create(
                    MissingDescriptionRule,
                    property.Locations.FirstOrDefault(),
                    property.Name));
                valid = false;
                continue;
            }

            var schema = BuildProperty(property, path, diagnostics, cancellationToken);
            if (schema is null)
            {
                valid = false;
                continue;
            }

            propertySchemas.Add(new KeyValuePair<string, string>(name, schema));
            if (FindAttribute(property, RequiredAttributeName) is not null)
            {
                required.Add(name);
            }
        }

        _ = path.Remove(model);
        if (!valid)
        {
            return null;
        }

        var builder = new StringBuilder();
        _ = builder.Append("{\"type\":\"object\"");
        AppendDescription(builder, model);
        if (propertySchemas.Count > 0)
        {
            _ = builder.Append(",\"properties\":{");
            for (var index = 0; index < propertySchemas.Count; index++)
            {
                if (index != 0)
                {
                    _ = builder.Append(',');
                }

                AppendJsonString(builder, propertySchemas[index].Key);
                _ = builder.Append(':').Append(propertySchemas[index].Value);
            }

            _ = builder.Append('}');
        }

        if (required.Count > 0)
        {
            _ = builder.Append(",\"required\":[");
            for (var index = 0; index < required.Count; index++)
            {
                if (index != 0)
                {
                    _ = builder.Append(',');
                }

                AppendJsonString(builder, required[index]);
            }

            _ = builder.Append(']');
        }

        if (policy == 0)
        {
            _ = builder.Append(",\"additionalProperties\":false");
        }

        return builder.Append('}').ToString();
    }

    private static bool ValidateModelShape(INamedTypeSymbol model, List<Diagnostic> diagnostics)
    {
        var valid = true;
        if (model.BaseType is { SpecialType: not SpecialType.System_Object })
        {
            diagnostics.Add(Diagnostic.Create(
                UnsupportedModelShapeRule,
                model.Locations.FirstOrDefault(),
                model.ToDisplayString(),
                "inheritance is not supported"));
            valid = false;
        }

        foreach (var member in model.GetMembers())
        {
            if (member is IFieldSymbol field && FindAttribute(field, JsonIncludeAttributeName) is not null)
            {
                ReportUnsupportedModelShape(field, "JsonInclude fields are not supported", diagnostics);
                valid = false;
                continue;
            }

            if (member is not IPropertySymbol property || property.IsStatic)
            {
                continue;
            }

            if (FindAttribute(property, JsonIgnoreAttributeName) is not null)
            {
                ReportUnsupportedModelShape(property, "JsonIgnore properties are not supported", diagnostics);
                valid = false;
                continue;
            }

            if (FindAttribute(property, JsonExtensionDataAttributeName) is not null)
            {
                ReportUnsupportedModelShape(property, "JsonExtensionData properties are not supported", diagnostics);
                valid = false;
                continue;
            }

            if (FindAttribute(property, JsonIncludeAttributeName) is not null)
            {
                ReportUnsupportedModelShape(property, "JsonInclude properties are not supported", diagnostics);
                valid = false;
                continue;
            }

            if (property.DeclaredAccessibility != Accessibility.Public)
            {
                continue;
            }

            if (property.IsIndexer)
            {
                ReportUnsupportedModelShape(property, "indexers are not supported", diagnostics);
                valid = false;
                continue;
            }

            if (property.GetMethod?.DeclaredAccessibility != Accessibility.Public ||
                property.SetMethod?.DeclaredAccessibility != Accessibility.Public)
            {
                ReportUnsupportedModelShape(property, "properties must have public getters and setters", diagnostics);
                valid = false;
            }
        }

        return valid;
    }

    private static void ReportUnsupportedModelShape(
        ISymbol symbol,
        string reason,
        List<Diagnostic> diagnostics) => diagnostics.Add(Diagnostic.Create(
            UnsupportedModelShapeRule,
            symbol.Locations.FirstOrDefault(),
            symbol.Name,
            reason));

    private static string? BuildProperty(
        IPropertySymbol property,
        HashSet<INamedTypeSymbol> path,
        List<Diagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        var kind = AppendType(builder, property.Type, property, path, diagnostics, cancellationToken);
        if (kind == SchemaKind.Unsupported)
        {
            return null;
        }

        var valid = AppendConstraints(builder, property, kind, diagnostics);
        AppendDescription(builder, property);
        return valid ? builder.Append('}').ToString() : null;
    }

    private static SchemaKind AppendType(
        StringBuilder builder,
        ITypeSymbol declaredType,
        IPropertySymbol property,
        HashSet<INamedTypeSymbol> path,
        List<Diagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        var type = UnwrapNullable(declaredType);
        switch (type.SpecialType)
        {
            case SpecialType.System_String:
                _ = builder.Append("{\"type\":\"string\"");
                return SchemaKind.String;
            case SpecialType.System_Boolean:
                _ = builder.Append("{\"type\":\"boolean\"");
                return SchemaKind.Boolean;
            case SpecialType.System_Int32:
            case SpecialType.System_Int64:
                _ = builder.Append("{\"type\":\"integer\"");
                return SchemaKind.Integer;
        }

        if (type is IArrayTypeSymbol array && array.Rank == 1)
        {
            _ = builder.Append("{\"type\":\"array\",\"items\":");
            var itemBuilder = new StringBuilder();
            var itemKind = AppendType(itemBuilder, array.ElementType, property, path, diagnostics, cancellationToken);
            if (itemKind == SchemaKind.Unsupported)
            {
                return SchemaKind.Unsupported;
            }

            _ = builder.Append(itemBuilder).Append('}');
            return SchemaKind.Array;
        }

        if (type is INamedTypeSymbol namedType && IsStringDictionary(namedType))
        {
            _ = builder.Append("{\"type\":\"object\",\"additionalProperties\":{\"type\":\"string\"}");
            return SchemaKind.StringDictionary;
        }

        if (type is INamedTypeSymbol nestedModel && nestedModel.TypeKind == TypeKind.Class)
        {
            if (FindAttribute(nestedModel, MarkerAttributeName) is null)
            {
                diagnostics.Add(Diagnostic.Create(
                    UnmarkedModelRule,
                    property.Locations.FirstOrDefault(),
                    property.Name,
                    nestedModel.ToDisplayString()));
                return SchemaKind.Unsupported;
            }

            var nestedJson = BuildModel(nestedModel, path, diagnostics, cancellationToken);
            if (nestedJson is null)
            {
                return SchemaKind.Unsupported;
            }

            _ = builder.Append(nestedJson, 0, nestedJson.Length - 1);
            return SchemaKind.Object;
        }

        diagnostics.Add(Diagnostic.Create(
            UnsupportedTypeRule,
            property.Locations.FirstOrDefault(),
            property.Name,
            declaredType.ToDisplayString()));
        return SchemaKind.Unsupported;
    }

    private static bool AppendConstraints(
        StringBuilder builder,
        IPropertySymbol property,
        SchemaKind kind,
        List<Diagnostic> diagnostics)
    {
        var valid = true;
        var minLength = FindAttribute(property, MinLengthAttributeName);
        if (minLength is not null)
        {
            if (kind != SchemaKind.String || !TryGetInt(minLength, out var value) || value < 0)
            {
                ReportInvalidConstraint(property, "ToolMinLength requires a string property and a non-negative value", diagnostics);
                valid = false;
            }
            else
            {
                _ = builder.Append(",\"minLength\":").Append(value.ToString(CultureInfo.InvariantCulture));
            }
        }

        var minimum = FindAttribute(property, MinimumAttributeName);
        var maximum = FindAttribute(property, MaximumAttributeName);
        var hasMinimum = TryGetLong(minimum, out var minimumValue);
        var hasMaximum = TryGetLong(maximum, out var maximumValue);
        if (minimum is not null)
        {
            if (kind != SchemaKind.Integer || !hasMinimum)
            {
                ReportInvalidConstraint(property, "ToolMinimum requires an int or long property", diagnostics);
                valid = false;
            }
            else
            {
                _ = builder.Append(",\"minimum\":").Append(minimumValue.ToString(CultureInfo.InvariantCulture));
            }
        }

        if (maximum is not null)
        {
            if (kind != SchemaKind.Integer || !hasMaximum)
            {
                ReportInvalidConstraint(property, "ToolMaximum requires an int or long property", diagnostics);
                valid = false;
            }
            else
            {
                _ = builder.Append(",\"maximum\":").Append(maximumValue.ToString(CultureInfo.InvariantCulture));
            }
        }

        if (kind == SchemaKind.Integer && hasMinimum && hasMaximum && minimumValue > maximumValue)
        {
            ReportInvalidConstraint(property, "ToolMinimum cannot exceed ToolMaximum", diagnostics);
            valid = false;
        }

        var pattern = FindAttribute(property, PatternAttributeName);
        if (pattern is not null)
        {
            if (kind != SchemaKind.String || !TryGetString(pattern, out var value))
            {
                ReportInvalidConstraint(property, "ToolPattern requires a string property and a non-null pattern", diagnostics);
                valid = false;
            }
            else
            {
                _ = builder.Append(",\"pattern\":");
                AppendJsonString(builder, value);
            }
        }

        var minItems = FindAttribute(property, MinItemsAttributeName);
        var maxItems = FindAttribute(property, MaxItemsAttributeName);
        var hasMinItems = TryGetInt(minItems, out var minimumItems);
        var hasMaxItems = TryGetInt(maxItems, out var maximumItems);
        if (minItems is not null)
        {
            if (kind != SchemaKind.Array || !hasMinItems || minimumItems < 0)
            {
                ReportInvalidConstraint(property, "ToolMinItems requires an array property and a non-negative value", diagnostics);
                valid = false;
            }
            else
            {
                _ = builder.Append(",\"minItems\":").Append(minimumItems.ToString(CultureInfo.InvariantCulture));
            }
        }

        if (maxItems is not null)
        {
            if (kind != SchemaKind.Array || !hasMaxItems || maximumItems < 0)
            {
                ReportInvalidConstraint(property, "ToolMaxItems requires an array property and a non-negative value", diagnostics);
                valid = false;
            }
            else
            {
                _ = builder.Append(",\"maxItems\":").Append(maximumItems.ToString(CultureInfo.InvariantCulture));
            }
        }

        if (kind == SchemaKind.Array && hasMinItems && hasMaxItems && minimumItems > maximumItems)
        {
            ReportInvalidConstraint(property, "ToolMinItems cannot exceed ToolMaxItems", diagnostics);
            valid = false;
        }

        var stringEnum = FindAttribute(property, StringEnumAttributeName);
        if (stringEnum is not null)
        {
            if (kind != SchemaKind.String || stringEnum.ConstructorArguments.Length != 1 ||
                stringEnum.ConstructorArguments[0].Kind != TypedConstantKind.Array ||
                stringEnum.ConstructorArguments[0].Values.IsDefaultOrEmpty ||
                stringEnum.ConstructorArguments[0].Values.Any(value => value.Value is not string))
            {
                ReportInvalidConstraint(property, "ToolStringEnum requires a string property and at least one non-null value", diagnostics);
                valid = false;
            }
            else
            {
                _ = builder.Append(",\"enum\":[");
                var values = stringEnum.ConstructorArguments[0].Values;
                for (var index = 0; index < values.Length; index++)
                {
                    if (index != 0)
                    {
                        _ = builder.Append(',');
                    }

                    AppendJsonString(builder, (string)values[index].Value!);
                }

                _ = builder.Append(']');
            }
        }

        valid &= AppendDefault(builder, property, kind, diagnostics);
        return valid;
    }

    private static bool AppendDefault(
        StringBuilder builder,
        IPropertySymbol property,
        SchemaKind kind,
        List<Diagnostic> diagnostics)
    {
        var boolDefault = FindAttribute(property, DefaultBoolAttributeName);
        var longDefault = FindAttribute(property, DefaultLongAttributeName);
        var stringDefault = FindAttribute(property, DefaultStringAttributeName);
        var count = (boolDefault is null ? 0 : 1) + (longDefault is null ? 0 : 1) + (stringDefault is null ? 0 : 1);
        if (count == 0)
        {
            return true;
        }

        if (count != 1)
        {
            ReportInvalidConstraint(property, "only one tool default attribute may be used", diagnostics);
            return false;
        }

        if (boolDefault is not null && kind == SchemaKind.Boolean &&
            boolDefault.ConstructorArguments.Length == 1 && boolDefault.ConstructorArguments[0].Value is bool boolValue)
        {
            _ = builder.Append(",\"default\":").Append(boolValue ? "true" : "false");
            return true;
        }

        if (longDefault is not null && kind == SchemaKind.Integer && TryGetLong(longDefault, out var longValue))
        {
            _ = builder.Append(",\"default\":").Append(longValue.ToString(CultureInfo.InvariantCulture));
            return true;
        }

        if (stringDefault is not null && kind == SchemaKind.String && TryGetString(stringDefault, out var stringValue))
        {
            _ = builder.Append(",\"default\":");
            AppendJsonString(builder, stringValue);
            return true;
        }

        ReportInvalidConstraint(property, "tool default attribute must match the property type", diagnostics);
        return false;
    }

    private static string BuildSource(INamedTypeSymbol model, string json)
    {
        var builder = new StringBuilder();
        if (!model.ContainingNamespace.IsGlobalNamespace)
        {
            _ = builder.Append("namespace ")
                .Append(model.ContainingNamespace.ToDisplayString())
                .AppendLine(";")
                .AppendLine();
        }

        var types = new List<INamedTypeSymbol>();
        for (var type = model; type is not null; type = type.ContainingType)
        {
            types.Add(type);
        }

        types.Reverse();
        for (var index = 0; index < types.Count; index++)
        {
            AppendIndent(builder, index);
            AppendTypeDeclaration(builder, types[index]);
            _ = builder.AppendLine();
            AppendIndent(builder, index);
            _ = builder.AppendLine("{");
        }

        AppendIndent(builder, types.Count);
        _ = builder.Append(GetAccessibility(model.DeclaredAccessibility))
            .Append(" static string Descriptor => ")
            .Append(ToCSharpString(json))
            .AppendLine(";");

        for (var index = types.Count - 1; index >= 0; index--)
        {
            AppendIndent(builder, index);
            _ = builder.AppendLine("}");
        }

        return builder.ToString();
    }

    private static void AppendTypeDeclaration(StringBuilder builder, INamedTypeSymbol type)
    {
        _ = builder.Append(GetAccessibility(type.DeclaredAccessibility)).Append(' ');
        if (type.IsStatic)
        {
            _ = builder.Append("static ");
        }
        else if (type.TypeKind == TypeKind.Class)
        {
            if (type.IsAbstract)
            {
                _ = builder.Append("abstract ");
            }

            if (type.IsSealed)
            {
                _ = builder.Append("sealed ");
            }
        }
        else if (type.TypeKind == TypeKind.Struct)
        {
            if (type.IsReadOnly)
            {
                _ = builder.Append("readonly ");
            }

            if (type.IsRefLikeType)
            {
                _ = builder.Append("ref ");
            }
        }

        _ = builder.Append("partial ").Append(GetTypeKind(type)).Append(' ').Append(type.Name);
    }

    private static string GetTypeKind(INamedTypeSymbol type)
    {
        if (type.IsRecord)
        {
            return type.TypeKind == TypeKind.Struct ? "record struct" : "record class";
        }

        return type.TypeKind switch
        {
            TypeKind.Class => "class",
            TypeKind.Struct => "struct",
            TypeKind.Interface => "interface",
            _ => throw new InvalidOperationException($"Unsupported containing type kind '{type.TypeKind}'."),
        };
    }

    private static void AppendIndent(StringBuilder builder, int depth) =>
        _ = builder.Append(' ', depth * 4);

    private static string GetAccessibility(Accessibility accessibility) => accessibility switch
    {
        Accessibility.Public => "public",
        Accessibility.Internal => "internal",
        Accessibility.Private => "private",
        Accessibility.Protected => "protected",
        Accessibility.ProtectedOrInternal => "protected internal",
        Accessibility.ProtectedAndInternal => "private protected",
        _ => throw new InvalidOperationException($"Unsupported accessibility '{accessibility}'."),
    };

    private static string GetHintName(INamedTypeSymbol model)
    {
        var name = model.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var builder = new StringBuilder(name.Length + 24);
        foreach (var character in name)
        {
            _ = builder.Append(char.IsLetterOrDigit(character) ? character : '_');
        }

        return builder.Append(".Descriptor.g.cs").ToString();
    }

    private static string? GetPropertyName(IPropertySymbol property, List<Diagnostic> diagnostics)
    {
        var attribute = FindAttribute(property, JsonPropertyNameAttributeName);
        if (attribute is null)
        {
            return property.Name;
        }

        if (TryGetString(attribute, out var name) && name.Length > 0)
        {
            return name;
        }

        ReportInvalidConstraint(property, "JsonPropertyName requires a non-empty name", diagnostics);
        return null;
    }

    private static bool HasDescription(ISymbol symbol) =>
        FindAttribute(symbol, DescriptionAttributeName) is { } attribute &&
        TryGetString(attribute, out var description) &&
        !string.IsNullOrWhiteSpace(description);

    private static void AppendDescription(StringBuilder builder, ISymbol symbol)
    {
        var attribute = FindAttribute(symbol, DescriptionAttributeName);
        if (attribute is not null && TryGetString(attribute, out var description) && description.Length > 0)
        {
            _ = builder.Append(",\"description\":");
            AppendJsonString(builder, description);
        }
    }

    private static void AppendJsonString(StringBuilder builder, string value)
    {
        _ = builder.Append('"');
        foreach (var character in value)
        {
            switch (character)
            {
                case '"':
                    _ = builder.Append("\\\"");
                    break;
                case '\\':
                    _ = builder.Append("\\\\");
                    break;
                case '\b':
                    _ = builder.Append("\\b");
                    break;
                case '\f':
                    _ = builder.Append("\\f");
                    break;
                case '\n':
                    _ = builder.Append("\\n");
                    break;
                case '\r':
                    _ = builder.Append("\\r");
                    break;
                case '\t':
                    _ = builder.Append("\\t");
                    break;
                default:
                    if (character < ' ')
                    {
                        _ = builder.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        _ = builder.Append(character);
                    }

                    break;
            }
        }

        _ = builder.Append('"');
    }

    private static string ToCSharpString(string value)
    {
        var builder = new StringBuilder(value.Length + 2);
        _ = builder.Append('"');
        foreach (var character in value)
        {
            _ = builder.Append(character switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                _ => character.ToString(),
            });
        }

        return builder.Append('"').ToString();
    }

    private static ITypeSymbol UnwrapNullable(ITypeSymbol type) =>
        type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable
            ? nullable.TypeArguments[0]
            : type;

    private static bool IsStringDictionary(INamedTypeSymbol type)
    {
        if (IsStringDictionaryInterface(type))
        {
            return true;
        }

        return type.AllInterfaces.Any(IsStringDictionaryInterface);
    }

    private static bool IsStringDictionaryInterface(INamedTypeSymbol type)
    {
        if (type.TypeArguments.Length != 2 ||
            type.TypeArguments[0].SpecialType != SpecialType.System_String ||
            type.TypeArguments[1].SpecialType != SpecialType.System_String)
        {
            return false;
        }

        var definition = type.OriginalDefinition.ToDisplayString();
        return definition is "System.Collections.Generic.Dictionary<TKey, TValue>" or
            "System.Collections.Generic.IDictionary<TKey, TValue>" or
            "System.Collections.Generic.IReadOnlyDictionary<TKey, TValue>";
    }

    private static AttributeData? FindAttribute(ISymbol symbol, string metadataName) =>
        symbol.GetAttributes().FirstOrDefault(attribute =>
            attribute.AttributeClass?.ToDisplayString() == metadataName);

    private static bool TryGetInt(AttributeData? attribute, out int value)
    {
        if (attribute is not null && attribute.ConstructorArguments.Length == 1 &&
            attribute.ConstructorArguments[0].Value is int result)
        {
            value = result;
            return true;
        }

        value = 0;
        return false;
    }

    private static bool TryGetLong(AttributeData? attribute, out long value)
    {
        if (attribute is not null && attribute.ConstructorArguments.Length == 1)
        {
            var argument = attribute.ConstructorArguments[0].Value;
            if (argument is long longValue)
            {
                value = longValue;
                return true;
            }

            if (argument is int intValue)
            {
                value = intValue;
                return true;
            }
        }

        value = 0;
        return false;
    }

    private static bool TryGetString(AttributeData attribute, out string value)
    {
        if (attribute.ConstructorArguments.Length == 1 && attribute.ConstructorArguments[0].Value is string result)
        {
            value = result;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static string GetSourcePath(IPropertySymbol property) =>
        property.Locations.FirstOrDefault(location => location.IsInSource)?.SourceTree?.FilePath ?? string.Empty;

    private static int GetSourcePosition(IPropertySymbol property) =>
        property.Locations.FirstOrDefault(location => location.IsInSource)?.SourceSpan.Start ?? int.MaxValue;

    private static void ReportInvalidConstraint(
        IPropertySymbol property,
        string message,
        List<Diagnostic> diagnostics) =>
        diagnostics.Add(Diagnostic.Create(
            InvalidConstraintRule,
            property.Locations.FirstOrDefault(),
            property.Name,
            message));
}
