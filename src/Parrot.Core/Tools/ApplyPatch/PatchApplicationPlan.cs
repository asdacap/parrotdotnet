namespace Parrot.Tools.ApplyPatch;

internal sealed class PatchApplicationPlan(IReadOnlyList<PatchMutation> mutations)
{
    public IReadOnlyList<PatchMutation> Mutations { get; } = mutations;
}
