namespace Parrot.Files;

internal enum FileMutationEntryKind
{
    Missing,
    Regular,
    Directory,
    SymbolicLink,
    Other,
}
