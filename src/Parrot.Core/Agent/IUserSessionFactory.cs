using Parrot.Llm;
using Parrot.Store;

namespace Parrot.Agent;

// A user session is created per claimed working directory, so the store names
// only the three things the claim decides: the id it settled on, the model, and
// the repository over that session's own database.
internal interface IUserSessionFactory
{
    UserSession Create(
        string id,
        ILLMProvider provider,
        string providerId,
        string model,
        string mode,
        EventRepository eventRepository);
}
