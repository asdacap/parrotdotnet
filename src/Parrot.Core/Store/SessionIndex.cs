using System.Text.Json;

namespace Parrot.Store;

internal sealed class SessionIndex(UserSessionResources resources)
{
    public UserSessionResources Resources { get; } = resources ?? throw new ArgumentNullException(nameof(resources));

    public SessionMeta? Find()
    {
        if (!File.Exists(Resources.MetadataPath) || HasLink(Resources.Root) || HasLink(Resources.MetadataPath))
        {
            return null;
        }

        var metadata = JsonSerializer.Deserialize(
            File.ReadAllText(Resources.MetadataPath), StoreJsonContext.Default.SessionMeta);
        return metadata is not null && Owns(metadata) ? metadata : null;
    }

    public void Publish(SessionMeta metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        if (!Owns(metadata))
        {
            throw new InvalidOperationException("Session metadata does not belong to these resources.");
        }

        var rootHasLink = Directory.Exists(Resources.Root) && HasLink(Resources.Root);
        var metadataHasLink = File.Exists(Resources.MetadataPath) && HasLink(Resources.MetadataPath);
        if (rootHasLink || metadataHasLink)
        {
            throw new InvalidOperationException("Session metadata paths cannot be symbolic links.");
        }

        _ = Directory.CreateDirectory(Resources.Root);
        var staged = Resources.MetadataPath + ".staging";
        File.WriteAllText(staged, JsonSerializer.Serialize(metadata, StoreJsonContext.Default.SessionMeta));
        File.Move(staged, Resources.MetadataPath, overwrite: true);
    }

    private static bool HasLink(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private bool Owns(SessionMeta metadata) =>
        UserSessionId.TryParse(metadata.Id, out var id)
        && Resources.Id.Equals(id)
        && OwnsWorkspace(metadata.WorkingDirectory);

    private bool OwnsWorkspace(string workingDirectory)
    {
        if (string.Equals(Resources.Workspace.LaunchDirectory, workingDirectory, StringComparison.Ordinal))
        {
            return true;
        }

        try
        {
            return ProjectWorkspace.FromLaunchDirectory(workingDirectory).Equals(Resources.Workspace);
        }
        catch (Exception failure) when (failure is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
