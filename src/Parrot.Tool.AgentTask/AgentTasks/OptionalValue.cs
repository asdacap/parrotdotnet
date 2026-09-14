namespace Parrot.AgentTasks;

internal readonly record struct OptionalValue<T>(bool IsSpecified, T? Value)
{
    internal static OptionalValue<T> Unspecified => new(false, default);

    internal static OptionalValue<T> From(T value) => new(true, value);
}
