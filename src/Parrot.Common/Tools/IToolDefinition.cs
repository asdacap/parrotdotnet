namespace Parrot.Tools;

/// <summary>The model-facing description and JSON Schema of one tool.</summary>
internal interface IToolDefinition
{
    string Description { get; }

    string ParametersJson { get; }
}
