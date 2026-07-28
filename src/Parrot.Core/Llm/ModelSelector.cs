namespace Parrot.Llm;

internal sealed record ModelSelector
{
    public ModelSelector(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            throw new ArgumentException("A model selector is required.", nameof(value));
        }

        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;
}
