namespace Parrot.Store;

internal sealed class SessionResourceLease : IDisposable, IAsyncDisposable
{
    private readonly IDisposable _activation;
    private readonly SessionDatabase _database;
    private bool _disposed;

    private SessionResourceLease(
        UserSessionResources resources,
        SessionDatabase database,
        IDisposable activation)
    {
        Resources = resources;
        _database = database;
        _activation = activation;
        var imageStore = new ImageArtifactStore(resources);
        Events = new EventRepository(database, imageStore, new AgentHistoryFiles(resources));
        Images = new ImageArtifactRepository(imageStore, Events);
        Images.RemoveStaleUnreferenced(DateTimeOffset.UtcNow.AddDays(-1));
    }

    public UserSessionResources Resources { get; }

    public EventRepository Events { get; }

    public ImageArtifactRepository Images { get; }

    public static SessionResourceLease Open(UserSessionResources resources, IDisposable activation)
    {
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(activation);

        try
        {
            return Open(resources, activation, SessionDatabase.Open(resources.DatabasePath));
        }
        catch
        {
            activation.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _database.Dispose();
        }
        finally
        {
            _activation.Dispose();
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    internal static SessionResourceLease Own(UserSessionResources resources, SessionDatabase database) =>
        Open(resources, EmptyActivation.Instance, database);

    private static SessionResourceLease Open(
        UserSessionResources resources,
        IDisposable activation,
        SessionDatabase database) =>
        new(resources, database, activation);

    private sealed class EmptyActivation : IDisposable
    {
        public static EmptyActivation Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
