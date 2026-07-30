namespace Parrot.Tools.Schema;

[AttributeUsage(AttributeTargets.Property, Inherited = false)]
internal sealed class ToolDefaultLongAttribute(long value) : Attribute
{
    public long Value { get; } = value;
}
