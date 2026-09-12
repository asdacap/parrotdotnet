namespace Parrot.Agent;

internal sealed class AgentSessionServices
{
    private readonly Dictionary<Type, object> _services = [];

    public void Register<T>(T? service)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(service);
        _services.Add(typeof(T), service);
    }

    public T GetService<T>()
        where T : class =>
        _services.TryGetValue(typeof(T), out var service)
            ? (T)service
            : throw new InvalidOperationException("The requested service is not registered in this agent scope.");
}
