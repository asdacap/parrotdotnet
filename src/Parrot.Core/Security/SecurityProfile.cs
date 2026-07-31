namespace Parrot.Security;

internal sealed class SecurityProfile
{
    private readonly SandboxRule[] _modeRules;
    private readonly SandboxRule[] _globalRules;
    private readonly SandboxRule[] _mandatoryRules;
    private readonly SandboxRule[] _runtimeCapabilities;

    private SecurityProfile(
        bool readOnly,
        IEnumerable<SandboxRule> modeRules,
        IEnumerable<SandboxRule> globalRules,
        IEnumerable<SandboxRule> mandatoryRules,
        IEnumerable<SandboxRule> runtimeCapabilities)
    {
        ReadOnly = readOnly;
        _modeRules = [.. modeRules];
        _globalRules = [.. globalRules];
        _mandatoryRules = [.. mandatoryRules];
        _runtimeCapabilities = [.. runtimeCapabilities];
    }

    public bool ReadOnly { get; }

    public IReadOnlyList<SandboxRule> Rules =>
        [.. PolicyRules(), .. _runtimeCapabilities];

    public IReadOnlyList<SandboxRule> RuntimeCapabilities => _runtimeCapabilities;

    public static SecurityProfile Compose(
        bool readOnly,
        IEnumerable<SandboxRule> modeRules,
        IEnumerable<SandboxRule> globalRules,
        IEnumerable<SandboxRule> runtimeCapabilities) =>
        Compose(readOnly, modeRules, globalRules, [], runtimeCapabilities);

    public static SecurityProfile Compose(
        bool readOnly,
        IEnumerable<SandboxRule> modeRules,
        IEnumerable<SandboxRule> globalRules,
        IEnumerable<SandboxRule> mandatoryRules,
        IEnumerable<SandboxRule> runtimeCapabilities)
    {
        var overrides = modeRules.Select(Normalize).ToArray();
        var mandatory = mandatoryRules.Select(Normalize).ToArray();
        var capabilities = runtimeCapabilities.Select(Normalize).ToArray();
        var configured = globalRules.Select(Normalize)
            .Where(rule => !readOnly || rule.Action != SandboxRuleAction.AllowWrite)
            .Where(rule => !capabilities.Any(capability => Overlaps(rule.Path, capability.Path)));

        return new(readOnly, overrides, configured, mandatory, capabilities);
    }

    public bool AllowsRead(string path) => Evaluate(path).Read;

    public bool AllowsWrite(string path) => Evaluate(path).Write;

    public SecurityProfile Add(SandboxRule runtimeCapability) =>
        Compose(ReadOnly, _modeRules, _globalRules, _mandatoryRules, _runtimeCapabilities.Append(runtimeCapability));

    public SecurityProfile WithRuntimeCapability(string path) =>
        Add(new(path, SandboxRuleAction.AllowWrite));

    public SecurityProfile WithoutRuntimeCapabilities() =>
        new(ReadOnly, _modeRules, _globalRules, _mandatoryRules, []);

    public bool AllowsDelegationTo(SecurityProfile target)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (ReadOnly && !target.ReadOnly)
        {
            return false;
        }

        var caller = WithoutRuntimeCapabilities();
        var child = target.WithoutRuntimeCapabilities();
        var paths = caller.PolicyRules().Concat(child.PolicyRules())
            .Select(rule => rule.Path)
            .Append(Path.DirectorySeparatorChar.ToString())
            .Distinct(StringComparer.Ordinal);

        return paths.All(path =>
            (!child.AllowsRead(path) || caller.AllowsRead(path)) &&
            (!child.AllowsWrite(path) || caller.AllowsWrite(path)));
    }

    private static bool Overlaps(string first, string second) =>
        Contains(first, second) || Contains(second, first);

    private static SandboxRule Normalize(SandboxRule rule)
    {
        if (!TryCanonicalize(rule.Path, out var canonical) || !Path.IsPathFullyQualified(rule.Path))
        {
            throw new ArgumentException("Sandbox rule paths must be absolute.", nameof(rule));
        }

        return rule with { Path = canonical };
    }

    private static bool TryCanonicalize(string path, out string canonical)
    {
        try
        {
            canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            return path.Length > 0;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            canonical = string.Empty;
            return false;
        }
    }

    private static bool Contains(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative == "." ||
            (!Path.IsPathRooted(relative) && relative != ".." &&
             !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
             !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal));
    }

    private IEnumerable<SandboxRule> OrderedBaseRules() =>
        _globalRules.Concat(_modeRules).OrderBy(rule => rule.Path.Length);

    private IEnumerable<SandboxRule> PolicyRules() =>
        OrderedBaseRules().Concat(_mandatoryRules);

    private (bool Read, bool Write) Evaluate(string path)
    {
        var access = (Read: true, Write: !ReadOnly);

        if (!TryCanonicalize(path, out var canonicalPath))
        {
            return (false, false);
        }

        foreach (var rule in PolicyRules().Concat(_runtimeCapabilities))
        {
            if (!Contains(rule.Path, canonicalPath))
            {
                continue;
            }

            access = rule.Action switch
            {
                SandboxRuleAction.AllowWrite => (true, true),
                SandboxRuleAction.DenyRead => (false, false),
                SandboxRuleAction.AllowRead => (true, access.Write),
                SandboxRuleAction.DenyWrite => (access.Read, false),
                _ => (false, false),
            };
        }

        return access;
    }
}
