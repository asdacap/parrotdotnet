namespace Parrot.Tools.ApplyPatch;

internal sealed class PatchMutation(PatchOperation operation, PatchMutationPath path, byte[]? before, byte[]? after)
{
    public PatchOperation Operation { get; } = operation;

    public PatchDiff.FileChange Change { get; } = new(path.Display, before, after);

    public byte[]? After { get; } = after;
}
