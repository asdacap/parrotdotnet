namespace Parrot.Security;

internal sealed class SecurityProfile
{
    private readonly SandboxRule[] _rules;

    private SecurityProfile(bool readOnly, IEnumerable<SandboxRule> rules)
    {
        ReadOnly = readOnly;
        _rules = [.. rules];
    }

    public bool ReadOnly { get; }

    public IReadOnlyList<SandboxRule> Rules => _rules;

    public static SecurityProfile Compose(
        bool readOnly,
        IEnumerable<SandboxRule> modeRules,
        IEnumerable<SandboxRule> globalRules,
        IEnumerable<SandboxRule> mandatoryRules)
    {
        var overrides = modeRules.Select(Normalize).ToArray();
        var mandatory = mandatoryRules.Select(Normalize).ToArray();
        var configured = globalRules.Select(Normalize);
        var rules = configured.Concat(overrides)
            .OrderBy(rule => rule.Path.Length)
            .Concat(mandatory)
            .ToArray();

        return new(readOnly, rules);
    }

    public static SecurityProfile ForAgent(
        SecurityProfile policy,
        IEnumerable<string> writableRoots,
        string scratchRoot,
        IEnumerable<SecurityWriteTarget> approvals)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(writableRoots);
        ArgumentException.ThrowIfNullOrWhiteSpace(scratchRoot);
        ArgumentNullException.ThrowIfNull(approvals);

        var approvalTargets = approvals.ToArray();
        foreach (var target in approvalTargets)
        {
            target.Validate();
        }

        var root = Path.GetPathRoot(Path.GetFullPath(scratchRoot))
            ?? throw new ArgumentException("The scratch root must have a filesystem root.", nameof(scratchRoot));
        var rules = new List<SandboxRule> { new(root, SandboxRuleAction.DenyWrite) };
        if (!policy.ReadOnly)
        {
            rules.AddRange(writableRoots.Select(path => new SandboxRule(path, SandboxRuleAction.AllowWrite)));
            rules.AddRange(approvalTargets.Select(target => new SandboxRule(target.Path, SandboxRuleAction.AllowWrite)));
        }

        rules.AddRange(policy._rules);
        rules.Add(new(scratchRoot, SandboxRuleAction.AllowWrite));
        return new(policy.ReadOnly, rules.Select(Normalize));
    }

    public bool AllowsRead(string path) => Evaluate(path).Read;

    public bool AllowsWrite(string path) => Evaluate(path).Write;

    public SecurityProfile RestrictWith(SecurityProfile child)
    {
        ArgumentNullException.ThrowIfNull(child);

        var readOnly = ReadOnly || child.ReadOnly;
        var paths = _rules.Concat(child._rules).Select(rule => rule.Path);
        var rules = CompileRules(
            readOnly,
            paths,
            path => Intersect(Evaluate(path), child.Evaluate(path)));

        return new(readOnly, rules);
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
}
