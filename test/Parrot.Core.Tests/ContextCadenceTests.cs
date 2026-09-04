using Parrot.Context;

namespace Parrot.Core.Tests;

internal sealed class ContextCadenceTests
{
    [Test]
    public async Task Emits_new_nonzero_bands_and_rebases_state()
    {
        var cadence = new ContextCadence();
        var initial = Snapshot(4);
        _ = await Assert.That(cadence.Observe(initial, "model", 10)).IsNull();
        _ = await Assert.That(cadence.Observe(Snapshot(24), "model", 11)).IsEqualTo(20);
        _ = await Assert.That(cadence.Observe(Snapshot(27), "model", 12)).IsEqualTo(25);
        _ = await Assert.That(cadence.Observe(Snapshot(43), "model", 13)).IsEqualTo(40);
        _ = await Assert.That(cadence.Observe(Snapshot(9), "model", 5)).IsNull();
        _ = await Assert.That(cadence.Observe(Snapshot(12), "model", 6)).IsNull();
        _ = await Assert.That(cadence.Observe(Snapshot(24), "model", 7)).IsEqualTo(20);
    }

    [Test]
    public async Task Changes_and_acknowledgement_rebase_without_duplicate()
    {
        var cadence = new ContextCadence();
        _ = cadence.Observe(Snapshot(24), "model", 1);
        _ = await Assert.That(cadence.Observe(Snapshot(29), "model", 2)).IsEqualTo(25);
        cadence.Acknowledge(Snapshot(34), "model", 2);
        _ = await Assert.That(cadence.Observe(Snapshot(36), "model", 3)).IsEqualTo(35);
        _ = await Assert.That(cadence.Observe(Snapshot(51), "other", 4)).IsNull();
        _ = await Assert.That(cadence.Observe(Snapshot(56), "other", 5)).IsEqualTo(55);
        _ = await Assert.That(cadence.Observe(new ContextSnapshot(1, 0, null, 90), "other", 6)).IsNull();
    }

    [Test]
    public async Task Durable_checkpoint_suppresses_restart_duplicate_and_allows_new_band()
    {
        var cadence = new ContextCadence();
        cadence.Restore(new ContextReminderCheckpoint("model", 100, 25));

        _ = await Assert.That(cadence.Observe(Snapshot(29), "model", 10)).IsNull();
        _ = await Assert.That(cadence.Observe(Snapshot(36), "model", 11)).IsEqualTo(35);
        _ = await Assert.That(cadence.Observe(Snapshot(41), "model", 12)).IsEqualTo(40);
    }

    [Test]
    public async Task Parent_and_child_state_is_independent()
    {
        var parent = new ContextCadence();
        var child = new ContextCadence();
        _ = parent.Observe(Snapshot(4), "model", 1);
        _ = child.Observe(Snapshot(4), "model", 1);
        _ = await Assert.That(parent.Observe(Snapshot(24), "model", 2)).IsEqualTo(20);
        _ = await Assert.That(child.Observe(Snapshot(9), "model", 2)).IsEqualTo(5);
    }

    private static ContextSnapshot Snapshot(int usage) => new(usage, 100, usage, 90);
}
