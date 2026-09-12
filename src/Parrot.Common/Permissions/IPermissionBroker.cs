using Parrot.Agent;
using Parrot.Security;

namespace Parrot.Permissions;

/// <summary>Coordinates write permission requests and their pending decisions for a session.</summary>
internal interface IPermissionBroker : IDisposable
{
    /// <summary>Requests a decision for the supplied write targets.</summary>
    Task<PermissionReply> Request(
        AgentIdentity identity,
        AgentSessionSecurity security,
        string reason,
        IReadOnlyList<SecurityWriteTarget> targets,
        CancellationToken cancellationToken);

    /// <summary>Returns pending interactive permission requests in stable order.</summary>
    IReadOnlyList<PermissionPending> Pending();

    /// <summary>Settles a pending request using the selected choice and reason.</summary>
    void Reply(string requestId, string choiceValue, string reason);
}
