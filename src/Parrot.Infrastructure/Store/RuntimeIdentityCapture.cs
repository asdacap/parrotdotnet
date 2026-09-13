using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Parrot.Store;

internal static class RuntimeIdentityCapture
{
    public static RuntimeIdentity Capture(string hostKey)
    {
        var hostIdentity = FingerprintHost(hostKey);
        var bootIdentity = ReadBootIdentity();
        var processId = Environment.ProcessId;
        var process = InspectProcess(processId);
        var processStart = process is { Status: ProcessIdentityStatus.Found, ProcessStartToken: not null }
            ? process.ProcessStartToken
            : throw new InvalidOperationException("The current process identity is unavailable.");

        return new RuntimeIdentity(
            hostIdentity,
            bootIdentity,
            processId,
            processStart,
            Guid.NewGuid().ToString("n", CultureInfo.InvariantCulture),
            InspectProcess);
    }

    public static string FingerprintHost(string hostKey) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(hostKey)));

    public static bool IsBootIdentityAvailable(string? bootIdentity)
    {
        if (bootIdentity is null or "unavailable")
        {
            return false;
        }

        const string legacyPrefix = "unavailable-";
        return !bootIdentity.StartsWith(legacyPrefix, StringComparison.Ordinal)
            || !long.TryParse(bootIdentity.AsSpan(legacyPrefix.Length), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var ticks)
            || !bootIdentity.AsSpan(legacyPrefix.Length).SequenceEqual(ticks.ToString(CultureInfo.InvariantCulture));
    }

    private static string ReadBootIdentity()
    {
        const string linuxBootIdentity = "/proc/sys/kernel/random/boot_id";
        if (File.Exists(linuxBootIdentity))
        {
            var value = File.ReadAllText(linuxBootIdentity).Trim();
            if (value.Length > 0)
            {
                return value;
            }
        }

        return "unavailable";
    }

    private static ProcessIdentity InspectProcess(int processId)
    {
        if (processId <= 0)
        {
            return new ProcessIdentity(ProcessIdentityStatus.Missing, null);
        }

        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            if (process.HasExited)
            {
                return new ProcessIdentity(ProcessIdentityStatus.Missing, null);
            }

            return new ProcessIdentity(
                ProcessIdentityStatus.Found,
                process.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture));
        }
        catch (ArgumentException)
        {
            return new ProcessIdentity(ProcessIdentityStatus.Missing, null);
        }
        catch (InvalidOperationException)
        {
            return new ProcessIdentity(ProcessIdentityStatus.Unreadable, null);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return new ProcessIdentity(ProcessIdentityStatus.Unreadable, null);
        }
    }
}
