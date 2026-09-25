namespace Parrot.Agent;

/// <summary>Maintains an agent's titled exit reminders and renders prompts for turn completion.</summary>
internal interface IExitReminder
{
    /// <summary>Gets the titles of the exit reminders that are currently set.</summary>
    IReadOnlyList<string> Titles { get; }

    /// <summary>Sets or replaces the exit reminder with the given title and announces the change.</summary>
    Task Set(string title, string description, CancellationToken cancellationToken);

    /// <summary>Clears the exit reminder with the given title and announces the change; false when no such reminder is set.</summary>
    Task<bool> Clear(string title, CancellationToken cancellationToken);

    /// <summary>Builds the next rendered exit reminder prompt, or null when none is configured.</summary>
    string? Build();
}
