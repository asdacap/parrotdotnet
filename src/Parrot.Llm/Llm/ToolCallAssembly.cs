namespace Parrot.Llm;

// Mutable accumulator for one streamed tool call: id and name land in the first
// fragment, argument text builds across the rest.
internal sealed class ToolCallAssembly
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public System.Text.StringBuilder Arguments { get; } = new();
}
