namespace Parrot.Agent;

/// <summary>Names incoming activity; Cause is the phrase reported after "wait interrupted due to", or null for plain input.</summary>
internal sealed record IncomingActivity(string Name, string? Cause);
