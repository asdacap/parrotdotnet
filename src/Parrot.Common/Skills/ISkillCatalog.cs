namespace Parrot.Skills;

/// <summary>Discovers and manages the enabled skills visible to agent turns.</summary>
internal interface ISkillCatalog
{
    /// <summary>Captures the current discovered skill snapshot.</summary>
    SkillSnapshot Capture();

    /// <summary>Invalidates and captures a fresh discovered skill snapshot.</summary>
    SkillSnapshot Refresh();

    /// <summary>
    /// Persists whether the discovered skill at <paramref name="path"/> is enabled and returns its refreshed metadata.
    /// Throws KeyNotFoundException when the skill is not discovered before or after the change.
    /// </summary>
    SkillMetadata Configure(string path, bool enabled);

    /// <summary>Invalidates the cached discovered snapshot.</summary>
    void Invalidate();
}
