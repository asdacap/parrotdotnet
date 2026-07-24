namespace Parrot.Tools;

internal sealed class PatchOperation(
    PatchOperationKind kind,
    string path,
    string data,
    IReadOnlyList<PatchHunk> hunks)
{
    public PatchOperationKind Kind { get; } = kind;

    public string Path { get; } = path;

    public string Data { get; } = data;

    public IReadOnlyList<PatchHunk> Hunks { get; } = hunks;
}
