namespace Parrot.Store;

internal sealed record ClaimResult(string SessionId, ClaimDisposition Disposition);
