namespace Parrot.Store;

internal enum ClaimDisposition
{
    Fresh,
    Reclaimed,
    Live,
    Contended,
    SelectionRequired,
    Corrupt,
    Resumed,
}
