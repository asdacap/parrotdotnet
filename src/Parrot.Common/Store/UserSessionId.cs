using System.Diagnostics.CodeAnalysis;

namespace Parrot.Store;

internal sealed class UserSessionId : IEquatable<UserSessionId>
{
    private const int MaximumLength = 255;

    private UserSessionId(string value) => Value = value;

    public string Value { get; }

    public static UserSessionId Generate() => Parse(Identifier.UserSession());

    public static UserSessionId Parse(string value)
    {
        if (!TryParse(value, out var parsed))
        {
            throw new FormatException("Invalid user session id.");
        }

        return parsed;
    }

    public static bool TryParse(string? value, [NotNullWhen(true)] out UserSessionId? parsed)
    {
        parsed = null;

        if (string.IsNullOrEmpty(value) || value.Length > MaximumLength || Path.IsPathRooted(value)
            || value is "." or ".." || value.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var character in value)
        {
            if (character is '/' or '\\' || char.IsControl(character)
                || !(character is >= 'a' and <= 'z'
                    || character is >= 'A' and <= 'Z'
                    || character is >= '0' and <= '9'
                    || character is '-' or '_' or '.'))
            {
                return false;
            }
        }

        parsed = new UserSessionId(value);
        return true;
    }

    public bool Equals(UserSessionId? other) =>
        other is not null && string.Equals(Value, other.Value, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is UserSessionId other && Equals(other);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    public override string ToString() => Value;
}
