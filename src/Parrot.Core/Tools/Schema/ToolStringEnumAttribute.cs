namespace Parrot.Tools.Schema;

[AttributeUsage(AttributeTargets.Property, Inherited = false)]
internal sealed class ToolStringEnumAttribute(params string[] values) : Attribute
{
    public IReadOnlyList<string> Values { get; } = values;
}
