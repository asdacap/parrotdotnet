namespace Parrot.Agent;

/// <summary>Maintains an agent's exit reminder and renders prompts for turn completion.</summary>
internal interface IExitReminder
{
    /// <summary>Sets or clears the current exit reminder.</summary>
    void Set(string? reminder);

    /// <summary>Builds the next rendered exit reminder prompt, or null when none is configured.</summary>
    string? Build();
}
