using Parrot.Agent;

namespace Parrot.Store;

internal sealed record OpenedSession(UserSession Session, bool Loaded);
