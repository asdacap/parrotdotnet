using Parrot.Security;
using Parrot.Store;

namespace Parrot.Agent;

internal sealed class AgentSessionSecurity(
    SecurityProfile policy,
    ProjectWorkspace workspace,
    string userSessionRoot)
{
    private readonly Lock _gate = new();
    private readonly List<SecurityWriteTarget> _approvals = [];
    private SecurityProfile _policy = policy ?? throw new ArgumentNullException(nameof(policy));

    public SecurityProfile Capture(SecurityProfile policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        lock (_gate)
        {
            _policy = policy;
            return SecurityProfile.ForAgent(
                _policy,
                workspace.WritableRoots,
                userSessionRoot,
                ValidApprovals());
        }
    }

    public SecurityProfile Policy()
    {
        lock (_gate)
        {
            return _policy;
        }
    }

    public void Approve(IReadOnlyList<SecurityWriteTarget> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        var copied = targets.ToArray();
        foreach (var target in copied)
        {
            target.Validate();
        }

        lock (_gate)
        {
            if (_policy.ReadOnly)
            {
                throw new InvalidOperationException("The current security policy is read-only.");
            }

            foreach (var target in copied)
            {
                if (_approvals.Any(existing => existing.Contains(target)))
                {
                    continue;
                }

                _ = _approvals.RemoveAll(target.Contains);
                _approvals.Add(target);
            }
        }
    }

    private List<SecurityWriteTarget> ValidApprovals()
    {
        var valid = new List<SecurityWriteTarget>();
        foreach (var target in _approvals)
        {
            try
            {
                target.Validate();
                valid.Add(target);
            }
            catch (InvalidOperationException)
            {
            }
        }

        return valid;
    }
}
