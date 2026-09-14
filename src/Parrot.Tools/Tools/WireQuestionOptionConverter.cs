using System.Text.Json;
using System.Text.Json.Serialization;

namespace Parrot.Tools;

internal sealed class WireQuestionOptionConverter : JsonConverter<QuestionOption>
{
    public override QuestionOption Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var text = reader.GetString() ?? throw new JsonException("question options cannot be empty");
            if (string.IsNullOrWhiteSpace(text))
            {
                throw new JsonException("question options cannot be empty");
            }

            return new QuestionOption { Label = text };
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("question options must be a string or an object");
        }

        string? label = null;
        string? description = null;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException("question option objects must contain properties only");
            }

            var property = reader.GetString();
            if (!reader.Read())
            {
                throw new JsonException("question option property values cannot be missing");
            }

            if (property == "label")
            {
                label = reader.TokenType == JsonTokenType.String
                    ? reader.GetString()
                    : throw new JsonException("question option 'label' must be a string");
            }
            else if (property == "description")
            {
                description = reader.TokenType == JsonTokenType.String
                    ? reader.GetString()
                    : throw new JsonException("question option 'description' must be a string");
            }
            else
            {
                throw new JsonException($"unknown question option property: {property}");
            }
        }

        if (string.IsNullOrWhiteSpace(label))
        {
            throw new JsonException("question option requires a nonblank 'label'");
        }

        return new QuestionOption { Label = label, Description = description ?? string.Empty };
    }

    public override void Write(Utf8JsonWriter writer, QuestionOption value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Label);
}
