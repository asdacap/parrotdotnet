namespace Parrot.Skills;

public sealed class SkillParseException : Exception
{
    public SkillParseException()
    {
    }

    public SkillParseException(string message)
        : base(message)
    {
    }

    public SkillParseException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    internal SkillParseException(string path, string message, Exception? innerException)
        : base($"{path}: {message}", innerException)
    {
    }
}
