namespace Parrot.Store;

internal enum ClaimDisposition
{
    // Nothing had ever claimed this working directory.
    Fresh,

    // The previous owner's process is gone; its binding was taken over.
    Reclaimed,

    // A live process still owns it, so this one gets a session of its own.
    Live,

    // Another claimer won the same version. link() reported EEXIST.
    Contended,
}
