namespace Parrot.Skills;

/// <summary>Discovers and manages the enabled skills visible to agent turns.</summary>
internal interface ISkillCatalog
{
    /// <summary>Captures the current discovered skill snapshot.</summary>
    SkillSnapshot Capture();

    /// <summary>Invalidates and captures a fresh discovered skill snapshot.</summary>
    SkillSnapshot Refresh();

    /// <summary>Persists whether a discovered skill is enabled.</summary>
    void Configure(string path, bool enabled);

    /// <summary>Invalidates the cached discovered snapshot.</summary>
    void Invalidate();
}
