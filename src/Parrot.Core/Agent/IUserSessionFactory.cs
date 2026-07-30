using Parrot.Llm;
using Parrot.Store;

namespace Parrot.Agent;

// A user session is created per claimed working directory, so the store passes
// the id it settled on, the reserved root-agent name, the model, and the
// repository over that session's own database. The root-agent name is transport
// for lazy root construction, not a user-session property.
internal interface IUserSessionFactory
{
    UserSession Create(
        SessionResourceLease resources,
        string id,
        string rootAgentName,
        ResolvedModelSelection model,
        string mode,
        bool interactivePermissions);
}
