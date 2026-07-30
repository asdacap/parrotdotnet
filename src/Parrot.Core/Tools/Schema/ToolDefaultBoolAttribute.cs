namespace Parrot.Tools.Schema;

[AttributeUsage(AttributeTargets.Property, Inherited = false)]
internal sealed class ToolDefaultBoolAttribute(bool value) : Attribute
{
    public bool Value { get; } = value;
}
