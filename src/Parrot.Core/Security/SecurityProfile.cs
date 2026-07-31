namespace Parrot.Security;

internal sealed class SecurityProfile
{
    private readonly SandboxRule[] _rules;
    private readonly SandboxRule[] _rulesWithoutRuntimeCapabilities;
    private readonly SandboxRule[] _runtimeCapabilities;

    private SecurityProfile(
        bool readOnly,
        IEnumerable<SandboxRule> rules,
        IEnumerable<SandboxRule> rulesWithoutRuntimeCapabilities,
        IEnumerable<SandboxRule> runtimeCapabilities)
    {
        ReadOnly = readOnly;
        _rules = [.. rules];
        _rulesWithoutRuntimeCapabilities = [.. rulesWithoutRuntimeCapabilities];
        _runtimeCapabilities = [.. runtimeCapabilities];
    }

    public bool ReadOnly { get; }

    public IReadOnlyList<SandboxRule> Rules => _rules;

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
        var policyRules = configured.Concat(overrides)
            .OrderBy(rule => rule.Path.Length)
            .Concat(mandatory)
            .ToArray();

        return new(readOnly, policyRules.Concat(capabilities), policyRules, capabilities);
    }

    public bool AllowsRead(string path) => Evaluate(path).Read;

    public bool AllowsWrite(string path) => Evaluate(path).Write;

    public SecurityProfile Add(SandboxRule runtimeCapability)
    {
        var capability = Normalize(runtimeCapability);
        return new(
            ReadOnly,
            _rules.Append(capability),
            _rulesWithoutRuntimeCapabilities,
            _runtimeCapabilities.Append(capability));
    }

    public SecurityProfile WithRuntimeCapability(string path) =>
        Add(new(path, SandboxRuleAction.AllowWrite));

    public SecurityProfile WithoutRuntimeCapabilities() =>
        new(ReadOnly, _rulesWithoutRuntimeCapabilities, _rulesWithoutRuntimeCapabilities, []);

    public SecurityProfile RestrictWith(SecurityProfile child)
    {
        ArgumentNullException.ThrowIfNull(child);

        var childPolicy = child.WithoutRuntimeCapabilities();
        var readOnly = ReadOnly || childPolicy.ReadOnly;
        var policyPaths = _rulesWithoutRuntimeCapabilities
            .Concat(childPolicy._rules)
            .Select(rule => rule.Path);
        var effectivePaths = _rules
            .Concat(childPolicy._rules)
            .Select(rule => rule.Path);
        var rulesWithoutRuntimeCapabilities = CompileRules(
            readOnly,
            policyPaths,
            path => Intersect(
                Evaluate(path, ReadOnly, _rulesWithoutRuntimeCapabilities),
                childPolicy.Evaluate(path)));
        var rules = CompileRules(
            readOnly,
            effectivePaths,
            path => Intersect(Evaluate(path), childPolicy.Evaluate(path)));
        var runtimeCapabilities = CompileRuntimeCapabilities(childPolicy);

        return new(readOnly, rules, rulesWithoutRuntimeCapabilities, runtimeCapabilities);
    }

    public bool AllowsDelegationTo(SecurityProfile target)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (ReadOnly && !target.ReadOnly)
        {
            return false;
        }

        var paths = _rules.Concat(target._rules)
            .Select(rule => rule.Path)
            .Append(Path.DirectorySeparatorChar.ToString())
            .Distinct(StringComparer.Ordinal);

        return paths.All(path =>
            (!target.AllowsRead(path) || AllowsRead(path)) &&
            (!target.AllowsWrite(path) || AllowsWrite(path)));
    }

    private static SandboxRule[] CompileRules(
        bool readOnly,
        IEnumerable<string> paths,
        Func<string, (bool Read, bool Write)> evaluate)
    {
        var compiled = new List<SandboxRule>();

        foreach (var path in paths.Distinct(StringComparer.Ordinal).OrderBy(path => path.Length))
        {
            var current = Evaluate(path, readOnly, compiled);
            var required = evaluate(path);
            if (current == required)
            {
                continue;
            }

            compiled.Add(new(path, SelectAction(current, required)));
        }

        return [.. compiled];
    }

    private static SandboxRuleAction SelectAction(
        (bool Read, bool Write) current,
        (bool Read, bool Write) required) =>
        (current, required) switch
        {
            (_, (false, false)) => SandboxRuleAction.DenyRead,
            (_, (true, true)) => SandboxRuleAction.AllowWrite,
            ((false, false), (true, false)) => SandboxRuleAction.AllowRead,
            ((true, true), (true, false)) => SandboxRuleAction.DenyWrite,
            _ => throw new InvalidOperationException("Security profiles cannot grant write access without read access."),
        };

    private static (bool Read, bool Write) Intersect(
        (bool Read, bool Write) parent,
        (bool Read, bool Write) child) =>
        (parent.Read && child.Read, parent.Write && child.Write);

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

    private static (bool Read, bool Write) Evaluate(
        string path,
        bool readOnly,
        IEnumerable<SandboxRule> rules)
    {
        var access = (Read: true, Write: !readOnly);

        if (!TryCanonicalize(path, out var canonicalPath))
        {
            return (false, false);
        }

        foreach (var rule in rules)
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

    private (bool Read, bool Write) Evaluate(string path) => Evaluate(path, ReadOnly, _rules);

    private SandboxRule[] CompileRuntimeCapabilities(SecurityProfile childPolicy)
    {
        var positiveCapabilities = _runtimeCapabilities
            .Where(rule => rule.Action is SandboxRuleAction.AllowRead or SandboxRuleAction.AllowWrite)
            .ToArray();
        var paths = positiveCapabilities.Select(rule => rule.Path)
            .Concat(_rules.Select(rule => rule.Path))
            .Concat(childPolicy._rules.Select(rule => rule.Path))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path.Length);
        var compiled = new List<SandboxRule>();

        foreach (var path in paths)
        {
            if (!positiveCapabilities.Any(capability => Contains(capability.Path, path)))
            {
                continue;
            }

            var (read, write) = Intersect(Evaluate(path), childPolicy.Evaluate(path));
            if (!read)
            {
                continue;
            }

            var covering = compiled.LastOrDefault(capability => Contains(capability.Path, path));
            if (covering is not null && (covering.Action == SandboxRuleAction.AllowWrite || !write))
            {
                continue;
            }

            compiled.Add(new(
                path,
                write ? SandboxRuleAction.AllowWrite : SandboxRuleAction.AllowRead));
        }

        return [.. compiled];
    }
}
