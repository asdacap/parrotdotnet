namespace Parrot.Agent;

[AttributeUsage(AttributeTargets.Parameter)]
internal sealed class InjectionTagAttribute(string tag) : Attribute
{
    public string Tag { get; } = tag;
}
