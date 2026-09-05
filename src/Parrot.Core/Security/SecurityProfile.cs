namespace Parrot.Security;

internal sealed class SecurityProfile
{
    private readonly SandboxRule[] _rules;
    private readonly SecurityProfile[] _innerProfiles;
    private readonly AgentSecurityBoundary? _agentBoundary;

    private SecurityProfile(
        bool readOnly,
        IEnumerable<SandboxRule> rules,
        IEnumerable<SecurityProfile> innerProfiles,
        AgentSecurityBoundary? agentBoundary)
    {
        ReadOnly = readOnly;
        _rules = [.. rules];
        _innerProfiles = [.. innerProfiles];
        _agentBoundary = agentBoundary;
    }

    public bool ReadOnly { get; }

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
            .Concat(mandatory);

        return new(readOnly, rules, [], null);
    }

    public static SecurityProfile ForAgent(
        SecurityProfile policy,
        IEnumerable<string> writableRoots,
        string userSessionScratchRoot,
        IEnumerable<SecurityWriteTarget> approvals)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(writableRoots);
        ArgumentException.ThrowIfNullOrWhiteSpace(userSessionScratchRoot);
        ArgumentNullException.ThrowIfNull(approvals);

        var approvalTargets = approvals.ToArray();
        foreach (var target in approvalTargets)
        {
            target.Validate();
        }

        var boundary = new AgentSecurityBoundary(
            policy.ReadOnly ? [] : writableRoots.Select(NormalizePath),
            NormalizePath(userSessionScratchRoot),
            policy.ReadOnly ? [] : approvalTargets.Select(target => NormalizePath(target.Path)));
        return new(policy.ReadOnly, [], [policy], boundary);
    }

    public bool AllowsRead(string path) => Evaluate(path).Read;

    public bool AllowsWrite(string path) => Evaluate(path).Write;

    public SecurityProfile RestrictWith(SecurityProfile child)
    {
        ArgumentNullException.ThrowIfNull(child);
        return new(ReadOnly || child.ReadOnly, [], [this, child], null);
    }

    public bool AllowsDelegationTo(SecurityProfile target)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (ReadOnly && !target.ReadOnly)
        {
            return false;
        }

        var paths = Paths().Concat(target.Paths())
            .Append(Path.DirectorySeparatorChar.ToString())
            .Distinct(StringComparer.Ordinal);

        return paths.All(path =>
            (!target.AllowsRead(path) || AllowsRead(path)) &&
            (!target.AllowsWrite(path) || AllowsWrite(path)));
    }

    internal MaterializedSecurityProfile Materialize()
    {
        var root = Path.DirectorySeparatorChar.ToString();
        var paths = Paths()
            .Where(path => _agentBoundary is null || !Contains(_agentBoundary.ScratchRoot, path))
            .Append(_agentBoundary?.ScratchRoot ?? root)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path.Length);
        var materialized = new List<MaterializedSandboxRule>();
        foreach (var path in paths)
        {
            var required = EvaluateForSandbox(path);
            if (!string.Equals(path, root, StringComparison.Ordinal)
                || EvaluateMaterialized(path, materialized) != required)
            {
                materialized.Add(new(path, required.Read, required.Write));
            }
        }

        return new(materialized);
    }

    private static SandboxRule Normalize(SandboxRule rule) =>
        rule with { Path = NormalizePath(rule.Path) };

    private static string NormalizePath(string path)
    {
        if (!TryCanonicalize(path, out var canonical) || !Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("Sandbox rule paths must be absolute.", nameof(path));
        }

        return canonical;
    }

    private static bool TryCanonicalize(string path, out string canonical)
    {
        try
        {
            canonical = Path.TrimEndingDirectorySeparator(PlatformPath.Normalize(path));
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

    private static (bool Read, bool Write) EvaluateMaterialized(
        string path,
        IEnumerable<MaterializedSandboxRule> rules)
    {
        var access = (Read: true, Write: false);
        foreach (var rule in rules)
        {
            if (Contains(rule.Path, path))
            {
                access = (rule.Read, rule.Write);
            }
        }

        return access;
    }

    private Access Evaluate(string path)
    {
        if (!TryCanonicalize(path, out var canonicalPath))
        {
            return Access.Denied;
        }

        return _agentBoundary is null
            ? EvaluatePolicy(canonicalPath)
            : EvaluateAgent(canonicalPath, _agentBoundary);
    }

    private Access EvaluateCanonical(string path) => _agentBoundary is null
        ? EvaluatePolicy(path)
        : EvaluateAgent(path, _agentBoundary);

    private Access EvaluatePolicy(string path)
    {
        if (_innerProfiles.Length > 0)
        {
            var inner = _innerProfiles.Select(profile => profile.EvaluateCanonical(path)).ToArray();
            var read = inner.All(access => access.Read);
            var write = inner.All(access => access.Write);
            return new(read, write, write && inner.Any(access => access.WriteAuthorized));
        }

        var access = new Access(true, !ReadOnly, false);
        foreach (var rule in _rules)
        {
            if (!Contains(rule.Path, path))
            {
                continue;
            }

            access = rule.Action switch
            {
                SandboxRuleAction.AllowWrite => new(true, true, true),
                SandboxRuleAction.DenyRead => Access.Denied,
                SandboxRuleAction.AllowRead => access with { Read = true },
                SandboxRuleAction.DenyWrite => access with { Write = false, WriteAuthorized = false },
                _ => Access.Denied,
            };
        }

        return access;
    }

    private Access EvaluateAgent(string path, AgentSecurityBoundary boundary)
    {
        var policy = _innerProfiles[0].EvaluateCanonical(path);
        if (Contains(boundary.ScratchRoot, path))
        {
            return policy with { Write = true, WriteAuthorized = true };
        }

        var boundaryAllowsWrite = boundary.WritablePaths.Any(root => Contains(root, path));
        return policy with { Write = policy.Write && (policy.WriteAuthorized || boundaryAllowsWrite) };
    }

    private (bool Read, bool Write) EvaluateForSandbox(string path)
    {
        var access = Evaluate(path);
        return _agentBoundary is null
            ? (access.Read, access.Write && access.WriteAuthorized)
            : (access.Read, access.Write);
    }

    private IEnumerable<string> Paths()
    {
        foreach (var inner in _innerProfiles)
        {
            foreach (var path in inner.Paths())
            {
                yield return path;
            }
        }

        foreach (var rule in _rules)
        {
            yield return rule.Path;
        }

        if (_agentBoundary is not null)
        {
            foreach (var path in _agentBoundary.WritablePaths)
            {
                yield return path;
            }

            yield return _agentBoundary.ScratchRoot;
        }
    }

    private readonly record struct Access(bool Read, bool Write, bool WriteAuthorized)
    {
        public static Access Denied => new(false, false, false);
    }

    private sealed class AgentSecurityBoundary(
        IEnumerable<string> writableRoots,
        string scratchRoot,
        IEnumerable<string> approvals)
    {
        public string[] WritablePaths { get; } = [.. writableRoots, .. approvals];

        public string ScratchRoot { get; } = scratchRoot;
    }
}
