using Parrot.Diagnostics;
using Parrot.Llm;

namespace Parrot.Core.Tests;

internal sealed class LastRequestDumperTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "parrot-last-request-dumper-tests", Guid.NewGuid().ToString("n"));

    private readonly RecordingDiagnosticLog _diagnostics = new();

    public LastRequestDumperTests() => _ = Directory.CreateDirectory(_root);

    public void Dispose()
    {
        _diagnostics.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    [Test]
    [Arguments("first-body", "second-body")]
    [Arguments("{}", "{}")]
    public async Task Dump_replaces_the_file_with_the_latest_body(string first, string second, CancellationToken cancellationToken)
    {
        var path = Path.Combine(_root, "last_request.json");
        var dumper = new LastRequestDumper(path, _diagnostics);
        var firstBytes = System.Text.Encoding.UTF8.GetBytes(first);
        var secondBytes = System.Text.Encoding.UTF8.GetBytes(second);

        dumper.Dump(firstBytes);
        _ = await Assert.That(File.Exists(path)).IsTrue();
        _ = await Assert.That((await File.ReadAllBytesAsync(path, cancellationToken)).AsSpan().SequenceEqual(firstBytes)).IsTrue();

        dumper.Dump(secondBytes);
        _ = await Assert.That((await File.ReadAllBytesAsync(path, cancellationToken)).AsSpan().SequenceEqual(secondBytes)).IsTrue();
        _ = await Assert.That(File.Exists(path + ".tmp")).IsFalse();
        var files = Directory.GetFiles(_root);
        _ = await Assert.That(files.AsSpan().SequenceEqual([path])).IsTrue();
        _ = await Assert.That(_diagnostics.Entries).IsEmpty();
    }

    [Test]
    public async Task Dump_is_safe_under_concurrent_calls(CancellationToken cancellationToken)
    {
        var path = Path.Combine(_root, "last_request.json");
        var dumper = new LastRequestDumper(path, _diagnostics);
        var bodies = Enumerable.Range(0, 32)
            .Select(index => System.Text.Encoding.UTF8.GetBytes($"request-{index}" + new string('x', 1024)))
            .ToArray();

        _ = Parallel.For(0, bodies.Length, _ => dumper.Dump(bodies[_ % 7]));

        var winner = await File.ReadAllBytesAsync(path, cancellationToken);
        _ = await Assert.That(bodies.Any(b => winner.AsSpan().SequenceEqual(b))).IsTrue();
        _ = await Assert.That(File.Exists(path + ".tmp")).IsFalse();
        _ = await Assert.That(_diagnostics.Entries).IsEmpty();
    }

    [Test]
    public async Task Dump_reports_failures_without_throwing(CancellationToken cancellationToken)
    {
        var blocked = Path.Combine(_root, "blocked");
        await File.WriteAllTextAsync(blocked, "not-a-directory", cancellationToken);
        var dumper = new LastRequestDumper(Path.Combine(blocked, "last_request.json"), _diagnostics);

        dumper.Dump([1, 2, 3]);
        _ = await Assert.That(File.Exists(blocked)).IsTrue();
        _ = await Assert.That(_diagnostics.Entries.Count).IsEqualTo(1);
        var entry = _diagnostics.Entries[0];
        _ = await Assert.That(entry.Category).IsEqualTo("provider");
        _ = await Assert.That(entry.Operation).IsEqualTo("last_request_dump_failed");
        _ = await Assert.That(entry.Severity).IsEqualTo(DiagnosticSeverity.Error);
        _ = await Assert.That(entry.ErrorCode is not null).IsTrue();
    }

    private sealed class RecordingDiagnosticLog : IDiagnosticLog
    {
        private readonly object _gate = new();
        private readonly List<DiagnosticEvent> _entries = [];

        public IReadOnlyList<DiagnosticEvent> Entries
        {
            get
            {
                lock (_gate)
                {
                    return [.. _entries];
                }
            }
        }

        public void Write(DiagnosticEvent entry)
        {
            lock (_gate)
            {
                _entries.Add(entry);
            }
        }

        public void Dispose()
        {
        }
    }
}
