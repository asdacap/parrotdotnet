namespace Parrot.Tools.Schema;

[AttributeUsage(AttributeTargets.Class, Inherited = false)]
internal sealed class ToolInputModelAttribute(AdditionalPropertiesPolicy additionalProperties) : Attribute
{
    public AdditionalPropertiesPolicy AdditionalProperties { get; } = additionalProperties;
}
