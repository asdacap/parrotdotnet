namespace Parrot.Agent;

/// <summary>Already-rendered output produced by an active-work blocker.</summary>
internal sealed record ActiveWorkBlockerResult(string? WorkSection, string? Reminder);
