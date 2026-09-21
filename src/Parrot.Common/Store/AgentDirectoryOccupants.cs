using System.Collections.Immutable;

namespace Parrot.Store;

internal sealed record AgentDirectoryOccupants(ImmutableArray<string> NamePath, List<string> SessionIds);
