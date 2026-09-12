namespace Parrot.Store;

internal sealed record ProcessIdentity(ProcessIdentityStatus Status, string? ProcessStartToken);
