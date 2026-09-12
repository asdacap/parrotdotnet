namespace Parrot.Security;

internal sealed class MaterializedSecurityProfile(IEnumerable<MaterializedSandboxRule> rules)
{
    private readonly MaterializedSandboxRule[] _rules = [.. rules];

    public IReadOnlyList<MaterializedSandboxRule> Rules => _rules;
}
