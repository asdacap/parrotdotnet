namespace Parrot.Tools;

internal enum FileMutationEntryKind
{
    Missing,
    Regular,
    Directory,
    SymbolicLink,
    Other,
}
