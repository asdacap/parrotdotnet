namespace Parrot.Agent;

/// <summary>Owns work that must settle before its agent scope is torn down.</summary>
internal interface IAgentWorkOwner
{
    /// <summary>Prevents further starts, cancels owned work and waits for its settlement.</summary>
    Task Settle();
}
