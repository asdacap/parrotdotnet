namespace Parrot.Process;

internal sealed class HaikunatorNameGenerator
{
    private readonly global::Haikunator.Haikunator _haikunator = new();

    public string Next() => $"{_haikunator.Haikunate(tokenLength: 0)}-arse.dat";
}
