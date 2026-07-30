namespace Parrot.Tools.Schema;

[AttributeUsage(AttributeTargets.Property, Inherited = false)]
internal sealed class ToolPatternAttribute(string pattern) : Attribute
{
    public string Pattern { get; } = pattern;
}
