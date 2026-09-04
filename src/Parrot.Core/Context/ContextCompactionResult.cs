namespace Parrot.Context;

internal sealed record ContextCompactionResult(ContextSnapshot Context, bool Reduced);
