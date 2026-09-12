using Parrot.Diagnostics;

namespace Parrot.Store;

/// <summary>Owns the durable resources and services for one user session.</summary>
internal interface ISessionResourceLease : IDisposable, IAsyncDisposable
{
    UserSessionResources Resources { get; }

    IEventRepository Events { get; }

    IImageArtifactRepository Images { get; }

    IDiagnosticLog Diagnostics { get; }
}
