namespace Parrot.Tools.Schema;

[AttributeUsage(AttributeTargets.Property, Inherited = false)]
internal sealed class ToolMinItemsAttribute(int count) : Attribute
{
    public int Count { get; } = count;
}
