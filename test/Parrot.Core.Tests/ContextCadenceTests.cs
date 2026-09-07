using Parrot.Context;

namespace Parrot.Core.Tests;

internal sealed class ContextCadenceTests
{
    [Test]
    public async Task Emits_ten_percent_bands_starting_at_fifty_and_rebases_state()
    {
        var cadence = new ContextCadence();
        _ = await Assert.That(cadence.Observe(Snapshot(4), "model", 10)).IsNull();
        _ = await Assert.That(cadence.Observe(Snapshot(24), "model", 11)).IsNull();
        _ = await Assert.That(cadence.Observe(Snapshot(49), "model", 12)).IsNull();
        _ = await Assert.That(cadence.Observe(Snapshot(50), "model", 13)).IsEqualTo(50);
        _ = await Assert.That(cadence.Observe(Snapshot(55), "model", 14)).IsNull();
        _ = await Assert.That(cadence.Observe(Snapshot(59), "model", 15)).IsNull();
        _ = await Assert.That(cadence.Observe(Snapshot(60), "model", 16)).IsEqualTo(60);
        _ = await Assert.That(cadence.Observe(Snapshot(83), "model", 17)).IsEqualTo(80);
        _ = await Assert.That(cadence.Observe(Snapshot(9), "model", 5)).IsNull();
        _ = await Assert.That(cadence.Observe(Snapshot(12), "model", 6)).IsNull();
        _ = await Assert.That(cadence.Observe(Snapshot(49), "model", 7)).IsNull();
        _ = await Assert.That(cadence.Observe(Snapshot(50), "model", 8)).IsEqualTo(50);
    }

    [Test]
    public async Task Changes_and_acknowledgement_rebase_without_duplicate()
    {
        var cadence = new ContextCadence();
        _ = cadence.Observe(Snapshot(49), "model", 1);
        _ = await Assert.That(cadence.Observe(Snapshot(59), "model", 2)).IsEqualTo(50);
        cadence.Acknowledge(Snapshot(64), "model", 2);
        _ = await Assert.That(cadence.Observe(Snapshot(69), "model", 3)).IsNull();
        _ = await Assert.That(cadence.Observe(Snapshot(70), "model", 4)).IsEqualTo(70);
        _ = await Assert.That(cadence.Observe(Snapshot(51), "other", 5)).IsNull();
        _ = await Assert.That(cadence.Observe(Snapshot(60), "other", 6)).IsEqualTo(60);
        _ = await Assert.That(cadence.Observe(new ContextSnapshot(1, 0, null, 90), "other", 7)).IsNull();
    }

    [Test]
    [Arguments(50)]
    [Arguments(55)]
    public async Task Durable_checkpoint_suppresses_restart_duplicate_and_allows_new_band(int checkpointPercentage)
    {
        var cadence = new ContextCadence();
        cadence.Restore(new ContextReminderCheckpoint("model", 100, checkpointPercentage));

        _ = await Assert.That(cadence.Observe(Snapshot(49), "model", 10)).IsNull();
        _ = await Assert.That(cadence.Observe(Snapshot(59), "model", 11)).IsNull();
        _ = await Assert.That(cadence.Observe(Snapshot(66), "model", 12)).IsEqualTo(60);
        _ = await Assert.That(cadence.Observe(Snapshot(71), "model", 13)).IsEqualTo(70);
    }

    [Test]
    public async Task Parent_and_child_state_is_independent()
    {
        var parent = new ContextCadence();
        var child = new ContextCadence();
        _ = parent.Observe(Snapshot(4), "model", 1);
        _ = child.Observe(Snapshot(4), "model", 1);
        _ = await Assert.That(parent.Observe(Snapshot(64), "model", 2)).IsEqualTo(60);
        _ = await Assert.That(child.Observe(Snapshot(59), "model", 2)).IsEqualTo(50);
    }

    private static ContextSnapshot Snapshot(int usage) => new(usage, 100, usage, 90);
}
