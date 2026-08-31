using System.Buffers;
using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using Parrot.Llm;

namespace Parrot.Tools;

internal sealed class ToolDocumentationCatalog(IReadOnlyDictionary<string, ToolDocumentation> tools)
{
    private readonly ReadOnlyDictionary<string, ToolDocumentation> _tools =
        new(tools.ToDictionary(
            tool => tool.Key,
            tool => new ToolDocumentation(tool.Value.Description, tool.Value.Parameters),
            StringComparer.Ordinal));

    public IReadOnlyDictionary<string, ToolDocumentation> Tools => _tools;

    public IReadOnlyList<LLMToolDefinition> Document(IReadOnlyList<ITool> runtimeTools)
    {
        ArgumentNullException.ThrowIfNull(runtimeTools);
        var runtimeByName = new Dictionary<string, ITool>(StringComparer.Ordinal);
        foreach (var tool in runtimeTools)
        {
            if (!runtimeByName.TryAdd(tool.Name, tool))
            {
                throw new InvalidDataException($"tool '{tool.Name}' is registered more than once");
            }
        }

        foreach (var name in runtimeByName.Keys)
        {
            if (!_tools.ContainsKey(name))
            {
                throw new InvalidDataException($"tools.{name} is not documented");
            }
        }

        foreach (var name in _tools.Keys)
        {
            if (!runtimeByName.ContainsKey(name))
            {
                throw new InvalidDataException($"tools.{name} does not match a registered tool");
            }
        }

        return [.. runtimeTools.Select(tool =>
        {
            var documentation = _tools[tool.Name];
            return new LLMToolDefinition(
                tool.Name,
                documentation.Description,
                DocumentSchema(tool.Name, tool.ParametersJson, documentation.Parameters));
        })];
    }

    private static string DocumentSchema(
        string toolName,
        string schemaJson,
        IReadOnlyDictionary<string, ToolParameterDocumentation> parameters)
    {
        try
        {
            using var document = JsonDocument.Parse(schemaJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException($"tool '{toolName}' parameters must be a JSON Schema object");
            }

            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                _ = WriteSchema(writer, document.RootElement, parameters, $"tools.{toolName}.parameters", null);
            }

            return Encoding.UTF8.GetString(buffer.WrittenSpan);
        }
        catch (JsonException failure)
        {
            throw new InvalidDataException($"tool '{toolName}' parameters must be valid JSON", failure);
        }
    }

    private static bool WriteSchema(
        Utf8JsonWriter writer,
        JsonElement schema,
        IReadOnlyDictionary<string, ToolParameterDocumentation> nestedDocumentation,
        string path,
        string? description)
    {
        if (schema.ValueKind != JsonValueKind.Object)
        {
            schema.WriteTo(writer);
            if (nestedDocumentation.Count > 0)
            {
                throw new InvalidDataException($"{path}.properties does not match an object schema");
            }

            return false;
        }

        writer.WriteStartObject();
        var documentedProperties = false;
        foreach (var member in schema.EnumerateObject())
        {
            if (string.Equals(member.Name, "description", StringComparison.Ordinal))
            {
                throw new InvalidDataException($"tool schema at {path} contains a description outside configuration");
            }

            writer.WritePropertyName(member.Name);
            if (string.Equals(member.Name, "properties", StringComparison.Ordinal))
            {
                WriteProperties(writer, member.Value, nestedDocumentation, path);
                documentedProperties = true;
            }
            else if (string.Equals(member.Name, "items", StringComparison.Ordinal))
            {
                documentedProperties |= WriteSchema(writer, member.Value, nestedDocumentation, path, null);
            }
            else
            {
                WriteStructuralValue(writer, member.Value, path);
            }
        }

        if (nestedDocumentation.Count > 0 && !documentedProperties)
        {
            throw new InvalidDataException($"{path}.properties does not match an object schema");
        }

        if (description is not null)
        {
            writer.WriteString("description", description);
        }

        writer.WriteEndObject();
        return documentedProperties;
    }

    private static void WriteProperties(
        Utf8JsonWriter writer,
        JsonElement properties,
        IReadOnlyDictionary<string, ToolParameterDocumentation> documentation,
        string path)
    {
        if (properties.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"tool schema at {path} has invalid properties");
        }

        var schemaNames = properties.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var name in schemaNames)
        {
            if (!documentation.ContainsKey(name))
            {
                throw new InvalidDataException($"{path}.{name} is not documented");
            }
        }

        foreach (var name in documentation.Keys)
        {
            if (!schemaNames.Contains(name))
            {
                throw new InvalidDataException($"{path}.{name} does not match a schema property");
            }
        }

        writer.WriteStartObject();
        foreach (var property in properties.EnumerateObject())
        {
            var parameter = documentation[property.Name];
            writer.WritePropertyName(property.Name);
            _ = WriteSchema(
                writer,
                property.Value,
                parameter.Properties,
                $"{path}.{property.Name}",
                parameter.Description);
        }

        writer.WriteEndObject();
    }

    private static void WriteSchemaMap(Utf8JsonWriter writer, JsonElement value, string path)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            WriteStructuralValue(writer, value, path);
            return;
        }

        writer.WriteStartObject();
        foreach (var member in value.EnumerateObject())
        {
            writer.WritePropertyName(member.Name);
            WriteStructuralValue(writer, member.Value, path);
        }

        writer.WriteEndObject();
    }

    private static void WriteStructuralValue(Utf8JsonWriter writer, JsonElement value, string path)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var member in value.EnumerateObject())
                {
                    if (string.Equals(member.Name, "description", StringComparison.Ordinal))
                    {
                        throw new InvalidDataException($"tool schema at {path} contains a description outside configuration");
                    }

                    writer.WritePropertyName(member.Name);
                    if (string.Equals(member.Name, "properties", StringComparison.Ordinal))
                    {
                        WriteSchemaMap(writer, member.Value, path);
                    }
                    else
                    {
                        WriteStructuralValue(writer, member.Value, path);
                    }
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray())
                {
                    WriteStructuralValue(writer, item, path);
                }

                writer.WriteEndArray();
                break;
            default:
                value.WriteTo(writer);
                break;
        }
    }
}
