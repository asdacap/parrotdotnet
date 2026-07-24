using Parrot.Protocol;

namespace Parrot.Store;

// The outcome of admitting a prompt. A replay of an admission the sender
// already made carries no event, because nothing happened the second time --
// which is also the answer to "was it created".
internal sealed record Admission(AdmittedInput Input, Event? Published)
{
    public bool Created => Published is not null;
}
