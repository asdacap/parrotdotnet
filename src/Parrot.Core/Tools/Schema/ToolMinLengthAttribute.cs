namespace Parrot.Tools.Schema;

[AttributeUsage(AttributeTargets.Property, Inherited = false)]
internal sealed class ToolMinLengthAttribute(int length) : Attribute
{
    public int Length { get; } = length;
}
