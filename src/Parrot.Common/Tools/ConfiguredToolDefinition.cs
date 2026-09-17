namespace Parrot.Tools;

internal sealed record ConfiguredToolDefinition(string Description, string ParametersJson) : IToolDefinition;
