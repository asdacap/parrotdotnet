using System.Text.Json;

namespace Parrot.Auth;

// Credentials live in the private config directory as one whole-file JSON object,
// written atomically (temp file, fsync, rename) with restrictive permissions.
// Each entry is parsed and validated only when looked up, so a malformed entry
// fails loudly on its own without breaking the others. Port of Go's
// auth.FileStore.
internal sealed class FileCredentialStore(string path) : ICredentialStore, IDisposable
{
    private const int MaxStoreBytes = 16 << 20;
    private const UnixFileMode DirectoryPermissions = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode FilePermissions = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private readonly SemaphoreSlim _gate = new(1, 1);

    public void Dispose() => _gate.Dispose();

    public async ValueTask<Credential?> Get(string name, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var values = await Read(cancellationToken).ConfigureAwait(false);
            return values.TryGetValue(name, out var value) ? Parse(value) : null;
        }
        finally
        {
            _ = _gate.Release();
        }
    }

    public async ValueTask Set(string name, Credential credential, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(credential);
        credential.Validate();

        var value = JsonSerializer.SerializeToElement(credential, CredentialJsonContext.Default.Credential);
        await Mutate(values => values[name] = value, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask Delete(string name, CancellationToken cancellationToken) =>
        await Mutate(
            values =>
            {
                if (!values.Remove(name))
                {
                    throw new AuthException("auth: credential not found");
                }
            },
            cancellationToken).ConfigureAwait(false);

    public async ValueTask<IReadOnlyList<string>> List(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var values = await Read(cancellationToken).ConfigureAwait(false);
            return [.. values.Keys.OrderBy(name => name, StringComparer.Ordinal)];
        }
        finally
        {
            _ = _gate.Release();
        }
    }

    private static Credential Parse(JsonElement value)
    {
        Credential? credential;

        try
        {
            credential = value.Deserialize(CredentialJsonContext.Default.Credential);
        }
        catch (JsonException failure)
        {
            throw new AuthException("auth: malformed credential", failure);
        }

        if (credential is null)
        {
            throw new AuthException("auth: malformed credential");
        }

        credential.Validate();
        return credential;
    }

    private async ValueTask Mutate(Action<Dictionary<string, JsonElement>> change, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var values = await Read(cancellationToken).ConfigureAwait(false);
            change(values);
            await Write(values, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ = _gate.Release();
        }
    }

    private async ValueTask<Dictionary<string, JsonElement>> Read(CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        }

        var info = new FileInfo(path);

        if (info.Length > MaxStoreBytes)
        {
            throw new AuthException("auth: credential store exceeds byte limit");
        }

        var text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);

        CredentialStoreFile? file;

        try
        {
            file = JsonSerializer.Deserialize(text, CredentialJsonContext.Default.CredentialStoreFile);
        }
        catch (JsonException failure)
        {
            throw new AuthException("auth: malformed credential store", failure);
        }

        if (file is null || file.Version != Credential.CurrentVersion)
        {
            throw new AuthException("auth: malformed credential store");
        }

        return new Dictionary<string, JsonElement>(file.Credentials, StringComparer.Ordinal);
    }

    private async ValueTask Write(Dictionary<string, JsonElement> values, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path);

        if (!string.IsNullOrEmpty(directory))
        {
            _ = OperatingSystem.IsWindows()
                ? Directory.CreateDirectory(directory)
                : Directory.CreateDirectory(directory, DirectoryPermissions);
        }

        var file = new CredentialStoreFile { Version = Credential.CurrentVersion, Credentials = values };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(file, CredentialJsonContext.Default.CredentialStoreFile);
        var temporary = path + ".tmp";

        var options = new FileStreamOptions
        {
            Mode = System.IO.FileMode.Create,
            Access = FileAccess.Write,
            Share = FileShare.None,

            // WriteThrough bypasses the OS write cache, so the flush below is
            // durable without a synchronous fsync in this async path.
            Options = FileOptions.WriteThrough,
        };

        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = FilePermissions;
        }

        var stream = new FileStream(temporary, options);

        await using (stream.ConfigureAwait(false))
        {
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        File.Move(temporary, path, overwrite: true);

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, FilePermissions);
        }
    }
}
