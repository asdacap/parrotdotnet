using Parrot.Diagnostics;

namespace Parrot.Store;

internal sealed class SessionResourceLease : IDisposable, IAsyncDisposable
{
    private readonly IDisposable _activation;
    private readonly SessionDatabase _database;
    private bool _disposed;

    private SessionResourceLease(
        UserSessionResources resources,
        SessionDatabase database,
        IDisposable activation,
        IDiagnosticLog diagnostics)
    {
        Resources = resources;
        _database = database;
        _activation = activation;
        Diagnostics = diagnostics;
        var imageStore = new ImageArtifactStore(resources);
        Events = new EventRepository(database, imageStore);
        Images = new ImageArtifactRepository(imageStore, Events);
        Images.RemoveStaleUnreferenced(DateTimeOffset.UtcNow.AddDays(-1));
    }

    public UserSessionResources Resources { get; }

    public EventRepository Events { get; }

    public ImageArtifactRepository Images { get; }

    public IDiagnosticLog Diagnostics { get; }

    public static SessionResourceLease Open(
        UserSessionResources resources,
        IDisposable activation,
        IDiagnosticLog diagnostics)
    {
        SessionDatabase? database = null;
        try
        {
            database = SessionDatabase.Open(resources.DatabasePath);
            return new SessionResourceLease(resources, database, activation, diagnostics);
        }
        catch (Exception failure)
        {
            diagnostics.Write(new DiagnosticEvent("session", "open", DiagnosticSeverity.Error)
            {
                Outcome = "failed",
                ErrorCode = DiagnosticEvent.ClassifyFailure(failure),
            });
            try
            {
                database?.Dispose();
            }
            finally
            {
                try
                {
                    diagnostics.Dispose();
                }
                finally
                {
                    activation.Dispose();
                }
            }

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
            Diagnostics.Write(new DiagnosticEvent("session", "closed", DiagnosticSeverity.Information)
            {
                Outcome = "success",
            });
        }
        finally
        {
            try
            {
                Diagnostics.Dispose();
            }
            finally
            {
                _activation.Dispose();
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    internal static SessionResourceLease Own(
        UserSessionResources resources,
        SessionDatabase database,
        IDiagnosticLog diagnostics)
    {
        try
        {
            return new SessionResourceLease(resources, database, EmptyActivation.Instance, diagnostics);
        }
        catch
        {
            try
            {
                database.Dispose();
            }
            finally
            {
                diagnostics.Dispose();
            }

            throw;
        }
    }

    private sealed class EmptyActivation : IDisposable
    {
        public static EmptyActivation Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
