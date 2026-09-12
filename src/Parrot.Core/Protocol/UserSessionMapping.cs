namespace Parrot.Protocol;

internal static class UserSessionMapping
{
    internal static UserSession Map(Agent.IUserSession session, bool loaded) =>
        new() { Id = session.Id, Model = session.Model, Mode = session.Mode.Profile.Id, Loaded = loaded };
}
