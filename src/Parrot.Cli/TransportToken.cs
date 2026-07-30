using System.Security.Cryptography;
using System.Text;

namespace Parrot.Cli;

internal sealed class TransportToken
{
    private const int TokenBytes = 32;
    private readonly byte[] _value;

    private TransportToken(byte[] value) => _value = value;

    public string Bearer => Convert.ToHexString(_value).ToLowerInvariant();

    public static TransportToken Generate() => new(RandomNumberGenerator.GetBytes(TokenBytes));

    public static TransportToken Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        try
        {
            if (!OperatingSystem.IsWindows())
            {
                var mode = File.GetUnixFileMode(path);
                if ((mode & ~(UnixFileMode.UserRead | UnixFileMode.UserWrite)) != 0)
                {
                    throw new InvalidOperationException($"token file must be owner-only (0600): {path}");
                }
            }

            var encoded = File.ReadAllText(path, Encoding.UTF8).Trim();
            if (encoded.Length != TokenBytes * 2)
            {
                throw new InvalidOperationException("transport token must contain 64 hexadecimal characters");
            }

            return new(Convert.FromHexString(encoded));
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or FormatException)
        {
            throw new InvalidOperationException($"cannot read transport token file {path}: {failure.Message}", failure);
        }
    }

    public static TransportToken Write(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var token = Generate();
        var directory = Path.GetDirectoryName(path) ?? string.Empty;
        if (OperatingSystem.IsWindows())
        {
            _ = Directory.CreateDirectory(directory);
        }
        else
        {
            var directoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            _ = Directory.CreateDirectory(directory, directoryMode);
        }

        var options = new FileStreamOptions
        {
            Access = FileAccess.Write,
            Mode = FileMode.CreateNew,
        };

        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        try
        {
            using var stream = new FileStream(path, options);
            using var writer = new StreamWriter(stream, Encoding.UTF8);
            writer.Write(token.Bearer);
            writer.Write('\n');
            return token;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"cannot create transport token file {path}: {failure.Message}", failure);
        }
    }

    public bool Matches(string presented)
    {
        var presentedBytes = Encoding.ASCII.GetBytes(presented);
        var expectedBytes = Encoding.ASCII.GetBytes(Bearer);
        return CryptographicOperations.FixedTimeEquals(presentedBytes, expectedBytes);
    }
}
