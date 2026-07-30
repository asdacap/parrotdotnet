namespace Parrot.Tools.Schema;

[AttributeUsage(AttributeTargets.Property, Inherited = false)]
internal sealed class ToolDefaultStringAttribute(string value) : Attribute
{
    public string Value { get; } = value;
}
