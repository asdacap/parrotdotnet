using System.Globalization;
using Parrot.Diagnostics;
using Parrot.Llm;
using Parrot.State;
using Parrot.Store;

namespace Parrot.Core.Tests;

internal sealed class ProviderRequestLog : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    private readonly UserSessionResources _resources;
    private readonly IDiagnosticLog _log;

    public ProviderRequestLog()
    {
        _ = Directory.CreateDirectory(_directory);
        _resources = new UserSessionResources(
            new StatePaths(_directory, _directory, _directory),
            UserSessionId.Parse("request-session"),
            ProjectWorkspace.FromLaunchDirectory(_directory));
        _log = FileDiagnosticLog.OpenSession(_resources, "request-instance", TextWriter.Null, TimeProvider.System);
    }

    public ProviderSessions OpenSessions(string agent) => new(_log, agent);

    public async Task AssertAttempts(string agent, string[] transports, string[] outcomes, int calls)
    {
        var lines = await File.ReadAllLinesAsync(_resources.LogPath);
        var owned = lines.Where(line => line.Contains($"agent=\"{agent}\"", StringComparison.Ordinal)).ToArray();
        var starts = owned.Where(line => line.Contains("event=\"request_started\"", StringComparison.Ordinal)).ToArray();
        var finishes = owned.Where(line => line.Contains("event=\"request_finished\"", StringComparison.Ordinal)).ToArray();
        var callStarts = owned.Where(line => line.Contains("event=\"call_started\"", StringComparison.Ordinal)).ToArray();
        var allCallStarts = lines.Where(line => line.Contains("event=\"call_started\"", StringComparison.Ordinal)).ToArray();
        _ = await Assert.That(allCallStarts.Select(line => ReadField(line, "correlation")).Distinct().Count()).IsEqualTo(allCallStarts.Length);
        _ = await Assert.That(callStarts.Length).IsEqualTo(calls);
        _ = await Assert.That(owned.Count(line => line.Contains("event=\"call_finished\"", StringComparison.Ordinal))).IsEqualTo(calls);
        _ = await Assert.That(starts.Length).IsEqualTo(transports.Length);
        _ = await Assert.That(finishes.Length).IsEqualTo(outcomes.Length);
        _ = await Assert.That(starts.Select(line => ReadField(line, "request")).Distinct().Count()).IsEqualTo(starts.Length);
        for (var index = 0; index < starts.Length; index++)
        {
            var request = ReadField(starts[index], "request");
            var correlation = ReadField(starts[index], "correlation");
            _ = await Assert.That(request.Length).IsGreaterThan(0);
            _ = await Assert.That(ReadField(finishes[index], "request")).IsEqualTo(request);
            _ = await Assert.That(ReadField(finishes[index], "correlation")).IsEqualTo(correlation);
            _ = await Assert.That(callStarts.Count(line => ReadField(line, "correlation") == correlation)).IsEqualTo(1);
            _ = await Assert.That(ReadField(starts[index], "transport")).IsEqualTo(transports[index]);
            _ = await Assert.That(ReadField(finishes[index], "transport")).IsEqualTo(transports[index]);
            _ = await Assert.That(ReadField(finishes[index], "outcome")).IsEqualTo(outcomes[index]);
            _ = await Assert.That(finishes[index].Contains("duration_ms=\"", StringComparison.Ordinal)).IsTrue();
            var sizes = owned.Where(line => line.Contains("event=\"request_size\"", StringComparison.Ordinal)
                && ReadField(line, "request") == request).ToArray();
            _ = await Assert.That(sizes.Length).IsLessThanOrEqualTo(1);
            if (sizes.Length == 1)
            {
                _ = await Assert.That(ReadField(sizes[0], "correlation")).IsEqualTo(correlation);
                _ = await Assert.That(ReadField(sizes[0], "transport")).IsEqualTo(transports[index]);
                _ = await Assert.That(ReadField(sizes[0], "request_bytes")).IsEqualTo(ReadField(finishes[index], "request_bytes"));
            }
            else
            {
                _ = await Assert.That(finishes[index].Contains("request_bytes=", StringComparison.Ordinal)).IsFalse();
            }

            _ = await Assert.That(lines.Count(line => line.Contains($"request=\"{request}\"", StringComparison.Ordinal))).IsEqualTo(2 + sizes.Length);
        }

        var text = string.Join('\n', lines);
        _ = await Assert.That(text.Contains("private-sentinel", StringComparison.Ordinal)).IsFalse();
        _ = await Assert.That(text.Contains("access-token", StringComparison.Ordinal)).IsFalse();
        _ = await Assert.That(text.Contains("account-id", StringComparison.Ordinal)).IsFalse();
    }

    public async Task AssertByteCounts(string agent, long?[] requestBytes, long?[] responseBytes)
    {
        var lines = await File.ReadAllLinesAsync(_resources.LogPath);
        var finishes = lines.Where(line => line.Contains($"agent=\"{agent}\"", StringComparison.Ordinal)
            && line.Contains("event=\"request_finished\"", StringComparison.Ordinal)).ToArray();
        _ = await Assert.That(finishes.Length).IsEqualTo(requestBytes.Length);
        _ = await Assert.That(finishes.Length).IsEqualTo(responseBytes.Length);
        for (var index = 0; index < finishes.Length; index++)
        {
            if (requestBytes[index] is { } expectedRequestBytes)
            {
                _ = await Assert.That(ReadField(finishes[index], "request_bytes")).IsEqualTo(expectedRequestBytes.ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                _ = await Assert.That(finishes[index].Contains("request_bytes=", StringComparison.Ordinal)).IsFalse();
            }

            if (responseBytes[index] is { } expectedResponseBytes)
            {
                _ = await Assert.That(ReadField(finishes[index], "response_bytes")).IsEqualTo(expectedResponseBytes.ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                _ = await Assert.That(finishes[index].Contains("response_bytes=", StringComparison.Ordinal)).IsFalse();
            }
        }
    }

    public void Dispose()
    {
        _log.Dispose();
        Directory.Delete(_directory, true);
    }

    private static string ReadField(string line, string field) => line.Split($"{field}=\"", StringSplitOptions.None)[1].Split('"')[0];
}
