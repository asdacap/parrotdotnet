using Parrot.Context;

namespace Parrot.Core.Tests;

internal sealed class ContextCadenceTests
{
    [Test]
    public async Task Emits_ten_percent_bands_starting_at_fifty_and_rebases_state()
    {
        var cadence = new ContextCadence();
        _ = await Assert.That(cadence.Observe(new ContextSnapshot(4, 100, 4, 90), "model", 10)).IsNull();
        _ = await Assert.That(cadence.Observe(new ContextSnapshot(24, 100, 24, 90), "model", 11)).IsNull();
        _ = await Assert.That(cadence.Observe(new ContextSnapshot(49, 100, 49, 90), "model", 12)).IsNull();
        _ = await Assert.That(cadence.Observe(new ContextSnapshot(50, 100, 50, 90), "model", 13)).IsEqualTo(50);
        _ = await Assert.That(cadence.Observe(new ContextSnapshot(55, 100, 55, 90), "model", 14)).IsNull();
        _ = await Assert.That(cadence.Observe(new ContextSnapshot(59, 100, 59, 90), "model", 15)).IsNull();
        _ = await Assert.That(cadence.Observe(new ContextSnapshot(60, 100, 60, 90), "model", 16)).IsEqualTo(60);
        _ = await Assert.That(cadence.Observe(new ContextSnapshot(83, 100, 83, 90), "model", 17)).IsEqualTo(80);
        _ = await Assert.That(cadence.Observe(new ContextSnapshot(9, 100, 9, 90), "model", 5)).IsNull();
        _ = await Assert.That(cadence.Observe(new ContextSnapshot(12, 100, 12, 90), "model", 6)).IsNull();
        _ = await Assert.That(cadence.Observe(new ContextSnapshot(49, 100, 49, 90), "model", 7)).IsNull();
        _ = await Assert.That(cadence.Observe(new ContextSnapshot(50, 100, 50, 90), "model", 8)).IsEqualTo(50);
    }

    [Test]
    public async Task Changes_and_acknowledgement_rebase_without_duplicate()
    {
        var cadence = new ContextCadence();
        _ = cadence.Observe(new ContextSnapshot(49, 100, 49, 90), "model", 1);
        _ = await Assert.That(cadence.Observe(new ContextSnapshot(59, 100, 59, 90), "model", 2)).IsEqualTo(50);
        cadence.Acknowledge(new ContextSnapshot(64, 100, 64, 90), "model", 2);
        _ = await Assert.That(cadence.Observe(new ContextSnapshot(69, 100, 69, 90), "model", 3)).IsNull();
        _ = await Assert.That(cadence.Observe(new ContextSnapshot(70, 100, 70, 90), "model", 4)).IsEqualTo(70);
        _ = await Assert.That(cadence.Observe(new ContextSnapshot(51, 100, 51, 90), "other", 5)).IsNull();
        _ = await Assert.That(cadence.Observe(new ContextSnapshot(60, 100, 60, 90), "other", 6)).IsEqualTo(60);
        _ = await Assert.That(cadence.Observe(new ContextSnapshot(1, 0, null, 90), "other", 7)).IsNull();
    }

    [Test]
    [Arguments(50)]
    [Arguments(55)]
    public async Task Durable_checkpoint_suppresses_restart_duplicate_and_allows_new_band(int checkpointPercentage)
    {
        var cadence = new ContextCadence();
        cadence.Restore(new ContextReminderCheckpoint("model", 100, checkpointPercentage));

        _ = await Assert.That(cadence.Observe(new ContextSnapshot(49, 100, 49, 90), "model", 10)).IsNull();
        _ = await Assert.That(cadence.Observe(new ContextSnapshot(59, 100, 59, 90), "model", 11)).IsNull();
        _ = await Assert.That(cadence.Observe(new ContextSnapshot(66, 100, 66, 90), "model", 12)).IsEqualTo(60);
        _ = await Assert.That(cadence.Observe(new ContextSnapshot(71, 100, 71, 90), "model", 13)).IsEqualTo(70);
    }

    [Test]
    public async Task Parent_and_child_state_is_independent()
    {
        var parent = new ContextCadence();
        var child = new ContextCadence();
        _ = parent.Observe(new ContextSnapshot(4, 100, 4, 90), "model", 1);
        _ = child.Observe(new ContextSnapshot(4, 100, 4, 90), "model", 1);
        _ = await Assert.That(parent.Observe(new ContextSnapshot(64, 100, 64, 90), "model", 2)).IsEqualTo(60);
        _ = await Assert.That(child.Observe(new ContextSnapshot(59, 100, 59, 90), "model", 2)).IsEqualTo(50);
    }
}
