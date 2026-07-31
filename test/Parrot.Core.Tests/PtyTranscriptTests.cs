using System.Text;
using Parrot.Process;

namespace Parrot.Core.Tests;

internal sealed class PtyTranscriptTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "parrot-pty-transcript-tests", Guid.NewGuid().ToString("n"));

    public PtyTranscriptTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Test]
    public async Task Transcript_uses_utf16_offsets_while_decoding_split_utf8()
    {
        using var transcript = new PtyTranscript(_directory);
        const string expected = "a€😀z";
        var bytes = Encoding.UTF8.GetBytes(expected);

        foreach (var value in bytes)
        {
            transcript.Append([value]);
        }

        transcript.Complete();

        var (cursor, text) = transcript.Read(0);
        var (_, completed) = transcript.Read(cursor);
        _ = await Assert.That(transcript.Length).IsEqualTo(expected.Length);
        _ = await Assert.That(text).IsEqualTo(expected);
        _ = await Assert.That(cursor).IsEqualTo(expected.Length);
        _ = await Assert.That(completed).IsEmpty();
    }

    [Test]
    public async Task Returned_cursor_reads_output_appended_after_the_snapshot()
    {
        using var transcript = new PtyTranscript(_directory);
        transcript.Append(Encoding.UTF8.GetBytes("A😀"));

        var (cursor, first) = transcript.Read(0);
        transcript.Append(Encoding.UTF8.GetBytes("€B"));
        var (completedCursor, second) = transcript.Read(cursor);

        _ = await Assert.That(cursor).IsEqualTo("A😀".Length);
        _ = await Assert.That(completedCursor).IsEqualTo("A😀€B".Length);
        _ = await Assert.That(first + second).IsEqualTo("A😀€B");
    }

    [Test]
    public async Task Every_in_range_utf16_offset_is_accepted()
    {
        using var transcript = new PtyTranscript(_directory);
        transcript.Append(Encoding.UTF8.GetBytes("😀x"));

        var (cursor, text) = transcript.Read(1);

        _ = await Assert.That(cursor).IsEqualTo(3);
        _ = await Assert.That(text.Length).IsEqualTo(2);
        _ = await Assert.That(text[0]).IsEqualTo("😀"[1]);
        _ = await Assert.That(text[1]).IsEqualTo('x');
    }

    [Test]
    public async Task Completion_flushes_an_incomplete_utf8_sequence_and_rejects_more_output()
    {
        using var transcript = new PtyTranscript(_directory);
        transcript.Append([(byte)0xe2]);

        transcript.Complete();
        transcript.Complete();

        _ = await Assert.That(transcript.Read(0).Text).IsEqualTo("�");
        _ = await Assert.That(transcript.Length).IsEqualTo(1);
        _ = await Assert.That(() => transcript.Append([(byte)'x']))
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task Incremental_reads_are_bounded_without_splitting_a_surrogate_pair()
    {
        using var transcript = new PtyTranscript(_directory);
        var prefix = new string('a', PtyTranscript.MaximumReadCharacters - 1);
        var expected = prefix + "😀tail";
        transcript.Append(Encoding.UTF8.GetBytes(expected));
        transcript.Complete();

        var (firstCursor, first) = transcript.Read(0);
        var (secondCursor, second) = transcript.Read(firstCursor);
        var (_, completed) = transcript.Read(secondCursor);

        _ = await Assert.That(first.Length).IsEqualTo(PtyTranscript.MaximumReadCharacters + 1);
        _ = await Assert.That(first).IsEqualTo(prefix + "😀");
        _ = await Assert.That(second).IsEqualTo("tail");
        _ = await Assert.That(secondCursor).IsEqualTo(expected.Length);
        _ = await Assert.That(completed).IsEmpty();
    }

    [Test]
    public async Task Concurrent_reads_from_one_cursor_return_identical_snapshots()
    {
        using var transcript = new PtyTranscript(_directory);
        var expected = new string('界', PtyTranscript.MaximumReadCharacters + 100);
        transcript.Append(Encoding.UTF8.GetBytes(expected));
        using var start = new ManualResetEventSlim();

        var reads = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() =>
            {
                start.Wait();
                return transcript.Read(0);
            }))
            .ToArray();
        start.Set();
        var results = await Task.WhenAll(reads);

        _ = await Assert.That(results.Select(result => result.Cursor).Distinct()).HasSingleItem();
        _ = await Assert.That(results.Select(result => result.Text).Distinct()).HasSingleItem();
        _ = await Assert.That(results[0].Text).IsEqualTo(expected[..PtyTranscript.MaximumReadCharacters]);
    }

    [Test]
    public async Task Spilled_completion_reads_utf16_suffix_and_cleans_up()
    {
        var transcript = new PtyTranscript(_directory);
        const string consumed = "a😀€";
        var remaining = new string('界', PtyTranscript.MaximumReadCharacters + 1) + "\uFEFFtail";
        transcript.Append(Encoding.UTF8.GetBytes(consumed + remaining));
        transcript.Complete();
        var (cursor, output) = transcript.ReadOutput(consumed.Length);
        using var copy = new StringWriter();

        await output.CopyTo(copy, CancellationToken.None);

        _ = await Assert.That(cursor).IsEqualTo((consumed + remaining).Length);
        _ = await Assert.That(output.Length).IsEqualTo(remaining.Length);
        _ = await Assert.That(output.Spilled).IsTrue();
        _ = await Assert.That(copy.ToString()).IsEqualTo(remaining);
        _ = await Assert.That(Directory.EnumerateFiles(_directory, ".process-*.tmp")).HasSingleItem();

        transcript.Dispose();

        _ = await Assert.That(Directory.EnumerateFiles(_directory, ".process-*.tmp")).IsEmpty();
    }
}
