using System.Globalization;
using System.Text;
using Parrot.Diagnostics;
using Parrot.State;
using Parrot.Store;

namespace Parrot.Core.Tests;

internal sealed class FileDiagnosticLogTests
{
    [Test]
    public async Task Session_records_are_bounded_escaped_correlated_and_appended()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            _ = Directory.CreateDirectory(directory);
            var paths = new StatePaths(directory, directory, directory);
            var resources = new UserSessionResources(paths, UserSessionId.Parse("session-test"), ProjectWorkspace.FromLaunchDirectory(directory));
            using var error = new StringWriter(CultureInfo.InvariantCulture);
            var clock = new ControlledTimeProvider();
            using (var log = FileDiagnosticLog.OpenSession(resources, "instance-one", error, clock))
            {
                log.Write(new DiagnosticEvent("tools", "finished", DiagnosticSeverity.Error)
                {
                    AgentSessionId = "agent-one",
                    UserSessionId = "wrong-session",
                    CorrelationId = "call-one",
                    ToolName = "read\n\"\\\u001b\u2028",
                    Outcome = new string('x', 255) + "😀" + new string('x', 10000),
                    DurationMilliseconds = 123,
                    ErrorCode = DiagnosticEvent.ClassifyFailure(new IOException("sensitive-exception-sentinel")),
                });
                _ = await Assert.That((await File.ReadAllLinesAsync(resources.LogPath)).Length).IsEqualTo(1);
            }

            using (var log = FileDiagnosticLog.OpenSession(resources, "instance-two", error, clock))
            {
                log.Write(new DiagnosticEvent("session", "resumed", DiagnosticSeverity.Information));
            }

            var lines = await File.ReadAllLinesAsync(resources.LogPath);
            _ = await Assert.That(lines.Length).IsEqualTo(2);
            _ = await Assert.That(lines[0].StartsWith("1970-01-01T00:00:00.0000000+00:00 ERROR", StringComparison.Ordinal)).IsTrue();
            _ = await Assert.That(lines[0].Contains("session=\"session-test\" agent=\"agent-one\" correlation=\"call-one\"", StringComparison.Ordinal)).IsTrue();
            _ = await Assert.That(lines[0].Contains("tool=\"read\\u000a\\\"\\\\\\u001b\\u2028\"", StringComparison.Ordinal)).IsTrue();
            _ = await Assert.That(lines[0].Contains("[truncated]", StringComparison.Ordinal)).IsTrue();
            _ = await Assert.That(lines[0].Contains("duration_ms=\"123\" error=\"io\"", StringComparison.Ordinal)).IsTrue();
            _ = await Assert.That(lines[0].Contains("sensitive-exception-sentinel", StringComparison.Ordinal)).IsFalse();
            _ = await Assert.That(lines[0].Contains('�', StringComparison.Ordinal)).IsFalse();
            _ = await Assert.That(Encoding.UTF8.GetByteCount(lines[0]) < 4096).IsTrue();
            _ = await Assert.That(resources.Owns(resources.LogPath)).IsTrue();
            _ = await Assert.That(error.ToString()).IsEmpty();
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Test]
    public async Task Concurrent_global_writers_have_distinct_files_and_intact_lines()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            _ = Directory.CreateDirectory(directory);
            var paths = new StatePaths(directory, directory, directory);
            using var error = new StringWriter(CultureInfo.InvariantCulture);
            var firstId = FileDiagnosticLog.CreateInstanceId();
            var secondId = FileDiagnosticLog.CreateInstanceId();
            using var first = FileDiagnosticLog.OpenGlobal(paths, firstId, error, TimeProvider.System);
            using var second = FileDiagnosticLog.OpenGlobal(paths, secondId, error, TimeProvider.System);
            await Parallel.ForAsync(0, 200, (index, _) =>
            {
                first.Write(new DiagnosticEvent("process", "first", DiagnosticSeverity.Information) { Count = index });
                second.Write(new DiagnosticEvent("process", "second", DiagnosticSeverity.Warning) { Count = index });
                return ValueTask.CompletedTask;
            });
            var files = Directory.GetFiles(paths.LogDirectory);
            _ = await Assert.That(files.Length).IsEqualTo(2);
            foreach (var file in files)
            {
                var lines = await File.ReadAllLinesAsync(file);
                _ = await Assert.That(lines.Length).IsEqualTo(200);
                _ = await Assert.That(lines.Distinct(StringComparer.Ordinal).Count()).IsEqualTo(200);
                _ = await Assert.That(lines.All(line => line.EndsWith('"') && line.Contains(" count=\"", StringComparison.Ordinal))).IsTrue();
            }

            using var collision = FileDiagnosticLog.OpenGlobal(paths, firstId, error, TimeProvider.System);
            collision.Write(new DiagnosticEvent("process", "must-not-overwrite", DiagnosticSeverity.Error));
            _ = await Assert.That((await File.ReadAllLinesAsync(Path.Combine(paths.LogDirectory, $"parrot-{firstId}.log"))).Length).IsEqualTo(200);
            _ = await Assert.That(error.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length).IsEqualTo(1);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Rotation_preserves_order_and_disables_nonfatally_on_failure(bool obstructRotation)
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            _ = Directory.CreateDirectory(directory);
            var paths = new StatePaths(directory, directory, directory);
            var resources = new UserSessionResources(paths, UserSessionId.Parse("rotation"), ProjectWorkspace.FromLaunchDirectory(directory));
            _ = Directory.CreateDirectory(resources.Root);
            using (var file = File.Create(resources.LogPath))
            {
                file.SetLength(10 * 1024 * 1024);
            }

            await File.WriteAllTextAsync(resources.LogPath + ".1", "previous-one");
            await File.WriteAllTextAsync(resources.LogPath + ".2", "previous-two");
            if (obstructRotation)
            {
                _ = Directory.CreateDirectory(resources.LogPath + ".3");
            }
            else
            {
                await File.WriteAllTextAsync(resources.LogPath + ".3", "retired");
            }

            using var error = new StringWriter(CultureInfo.InvariantCulture);
            using var log = FileDiagnosticLog.OpenSession(resources, "instance", error, TimeProvider.System);
            log.Write(new DiagnosticEvent("session", "first", DiagnosticSeverity.Information));
            log.Write(new DiagnosticEvent("session", "second", DiagnosticSeverity.Information));
            if (obstructRotation)
            {
                _ = await Assert.That(error.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length).IsEqualTo(1);
                _ = await Assert.That(new FileInfo(resources.LogPath).Length).IsEqualTo(10L * 1024 * 1024);
            }
            else
            {
                _ = await Assert.That((await File.ReadAllLinesAsync(resources.LogPath)).Length).IsEqualTo(2);
                _ = await Assert.That(await File.ReadAllTextAsync(resources.LogPath + ".2")).IsEqualTo("previous-one");
                _ = await Assert.That(await File.ReadAllTextAsync(resources.LogPath + ".3")).IsEqualTo("previous-two");
                _ = await Assert.That(Directory.GetFiles(resources.Root).Length).IsEqualTo(4);
                _ = await Assert.That(Directory.GetFiles(resources.Root).All(file => new FileInfo(file).Length <= 10L * 1024 * 1024)).IsTrue();
                _ = await Assert.That(error.ToString()).IsEmpty();
            }
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Open_and_write_failures_do_not_escape_or_repeat_warnings(bool failDuringWrite)
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            _ = Directory.CreateDirectory(directory);
            var paths = new StatePaths(directory, directory, directory);
            var resources = new UserSessionResources(paths, UserSessionId.Parse("failure"), ProjectWorkspace.FromLaunchDirectory(directory));
            _ = Directory.CreateDirectory(resources.Root);
            if (failDuringWrite)
            {
                _ = File.CreateSymbolicLink(resources.LogPath, "/dev/full");
            }
            else
            {
                _ = Directory.CreateDirectory(resources.LogPath);
            }

            using var error = new StringWriter(CultureInfo.InvariantCulture);
            using (var log = FileDiagnosticLog.OpenSession(resources, "instance", error, TimeProvider.System))
            {
                log.Write(new DiagnosticEvent("session", "first", DiagnosticSeverity.Information));
                log.Write(new DiagnosticEvent("session", "second", DiagnosticSeverity.Information));
            }

            _ = await Assert.That(error.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length).IsEqualTo(1);
            _ = await Assert.That(error.ToString().Contains(directory, StringComparison.Ordinal)).IsFalse();
            using var disposedError = new StringWriter(CultureInfo.InvariantCulture);
            await disposedError.DisposeAsync();
            using var silent = FileDiagnosticLog.OpenSession(resources, "instance", disposedError, TimeProvider.System);
            silent.Write(new DiagnosticEvent("session", "ignored", DiagnosticSeverity.Information));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
