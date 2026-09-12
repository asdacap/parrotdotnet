using Parrot.Agent;

namespace Parrot.Store;

internal sealed record OpenedSession(IUserSession Session, bool Loaded);
