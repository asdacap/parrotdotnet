namespace Parrot.Tools.Schema;

[AttributeUsage(AttributeTargets.Property, Inherited = false)]
internal sealed class ToolMaximumAttribute(long value) : Attribute
{
    public long Value { get; } = value;
}
