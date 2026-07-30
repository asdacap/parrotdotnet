namespace Parrot.Protocol;

public sealed partial class UserSession
{
    internal static UserSession From(Agent.UserSession session, bool loaded) =>
        new() { Id = session.Id, Model = session.Model, Mode = session.Mode.Id, Loaded = loaded };
}
