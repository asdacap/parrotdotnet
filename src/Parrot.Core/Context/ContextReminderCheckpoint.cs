namespace Parrot.Context;

internal sealed record ContextReminderCheckpoint(
    string CanonicalModel,
    int ContextLimit,
    int Percentage);
