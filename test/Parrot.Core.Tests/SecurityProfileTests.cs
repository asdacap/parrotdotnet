using Parrot.Security;

namespace Parrot.Core.Tests;

internal sealed class SecurityProfileTests
{
    [Test]
    public async Task Access_preserves_global_restrictions_and_applies_narrower_mode_overrides()
    {
        var profile = SecurityProfile.Compose(
            readOnly: false,
            [
                new("/workspace", SandboxRuleAction.AllowWrite),
                new("/workspace/private/public", SandboxRuleAction.AllowRead),
            ],
            [
                new("/workspace/private", SandboxRuleAction.DenyRead),
                new("/workspace/generated", SandboxRuleAction.DenyWrite),
            ],
            []);

        _ = await Assert.That(profile.AllowsRead("/workspace/private/file")).IsFalse();
        _ = await Assert.That(profile.AllowsWrite("/workspace/private/file")).IsFalse();
        _ = await Assert.That(profile.AllowsRead("/workspace/private/public/../public/file")).IsTrue();
        _ = await Assert.That(profile.AllowsWrite("/workspace/private/public/file")).IsFalse();
        _ = await Assert.That(profile.AllowsRead("/workspace/generated/file")).IsTrue();
        _ = await Assert.That(profile.AllowsWrite("/workspace/generated/file")).IsFalse();
        _ = await Assert.That(profile.AllowsWrite("/workspace-other/file")).IsTrue();

        var equivalentPaths = SecurityProfile.Compose(
            readOnly: false,
            [new("/workspace/private", SandboxRuleAction.AllowRead)],
            [new("/workspace/private/", SandboxRuleAction.DenyRead)],
            []);
        _ = await Assert.That(equivalentPaths.AllowsRead("/workspace/private/file")).IsTrue();
    }

    [Test]
    public async Task Restriction_intersects_static_policies_and_is_monotonic()
    {
        var parent = SecurityProfile.Compose(
            readOnly: false,
            [new("/parent-private", SandboxRuleAction.DenyRead)],
            [],
            []);
        var child = SecurityProfile.Compose(
            readOnly: true,
            [
                new("/child-private", SandboxRuleAction.DenyRead),
                new("/parent-private/narrow", SandboxRuleAction.AllowWrite),
            ],
            [],
            []);
        var restricted = parent.RestrictWith(child);
        var grandchild = restricted.RestrictWith(SecurityProfile.Compose(false, [], [], []));

        _ = await Assert.That(restricted.ReadOnly).IsTrue();
        _ = await Assert.That(restricted.AllowsRead("/parent-private/narrow/file")).IsFalse();
        _ = await Assert.That(restricted.AllowsWrite("/parent-private/narrow/file")).IsFalse();
        _ = await Assert.That(restricted.AllowsRead("/child-private/file")).IsFalse();
        _ = await Assert.That(restricted.AllowsWrite("/ordinary/file")).IsFalse();
        _ = await Assert.That(grandchild.AllowsRead("/parent-private/narrow/file")).IsFalse();
        _ = await Assert.That(grandchild.AllowsRead("/child-private/file")).IsFalse();
        _ = await Assert.That(grandchild.AllowsWrite("/ordinary/file")).IsFalse();
    }

    [Test]
    public async Task Agent_profile_applies_ordered_roots_approvals_policy_and_scratch()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            var workspace = Directory.CreateDirectory(Path.Combine(root.FullName, "workspace")).FullName;
            var approved = Directory.CreateDirectory(Path.Combine(root.FullName, "approved")).FullName;
            var scratch = Directory.CreateDirectory(Path.Combine(root.FullName, "scratch")).FullName;
            var policy = SecurityProfile.Compose(
                false,
                [new SandboxRule(approved, SandboxRuleAction.DenyWrite), new SandboxRule(scratch, SandboxRuleAction.DenyWrite)],
                [],
                []);

            var effective = SecurityProfile.ForAgent(
                policy,
                [workspace],
                scratch,
                [SecurityWriteTarget.Resolve(approved)]);

            _ = await Assert.That(effective.AllowsWrite(Path.Combine(root.FullName, "ordinary"))).IsFalse();
            _ = await Assert.That(effective.AllowsWrite(Path.Combine(workspace, "file"))).IsTrue();
            _ = await Assert.That(effective.AllowsWrite(Path.Combine(approved, "file"))).IsFalse();
            _ = await Assert.That(effective.AllowsWrite(Path.Combine(scratch, "file"))).IsTrue();
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Test]
    public async Task Read_only_profile_keeps_global_write_grants()
    {
        var profile = SecurityProfile.Compose(
            readOnly: true,
            modeRules: [],
            globalRules: [new SandboxRule("/cache", SandboxRuleAction.AllowWrite)],
            mandatoryRules: []);

        _ = await Assert.That(profile.AllowsWrite("/cache/item")).IsTrue();
        _ = await Assert.That(profile.AllowsWrite("/workspace/item")).IsFalse();
    }

    [Test]
    public async Task Agent_profile_keeps_workspace_read_only_but_always_allows_scratch()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            var workspace = Directory.CreateDirectory(Path.Combine(root.FullName, "workspace")).FullName;
            var scratch = Directory.CreateDirectory(Path.Combine(root.FullName, "scratch")).FullName;
            var effective = SecurityProfile.ForAgent(
                SecurityProfile.Compose(true, [], [], []),
                [workspace],
                scratch,
                []);

            _ = await Assert.That(effective.AllowsWrite(Path.Combine(workspace, "file"))).IsFalse();
            _ = await Assert.That(effective.AllowsWrite(Path.Combine(scratch, "file"))).IsTrue();
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Test]
    public async Task Delegation_allows_narrower_effective_profiles_and_rejects_escalation()
    {
        var rules = new[] { new SandboxRule("/secret", SandboxRuleAction.DenyRead) };
        var readOnlyCaller = SecurityProfile.Compose(true, rules, [], []);
        var writableCaller = SecurityProfile.Compose(false, rules, [], []);
        var matchingReadOnly = SecurityProfile.Compose(true, rules, [], []);
        var matchingWritable = SecurityProfile.Compose(false, rules, [], []);
        var narrower = SecurityProfile.Compose(
            true,
            [.. rules, new SandboxRule("/extra", SandboxRuleAction.DenyRead)],
            [],
            []);
        var broader = SecurityProfile.Compose(true, [], [], []);

        _ = await Assert.That(readOnlyCaller.AllowsDelegationTo(matchingReadOnly)).IsTrue();
        _ = await Assert.That(matchingReadOnly.AllowsDelegationTo(readOnlyCaller)).IsTrue();
        _ = await Assert.That(readOnlyCaller.AllowsDelegationTo(matchingWritable)).IsFalse();
        _ = await Assert.That(readOnlyCaller.AllowsDelegationTo(narrower)).IsTrue();
        _ = await Assert.That(readOnlyCaller.AllowsDelegationTo(broader)).IsFalse();
        _ = await Assert.That(writableCaller.AllowsDelegationTo(matchingReadOnly)).IsTrue();
        _ = await Assert.That(writableCaller.AllowsDelegationTo(matchingWritable)).IsTrue();
        _ = await Assert.That(writableCaller.AllowsDelegationTo(narrower)).IsTrue();
        _ = await Assert.That(writableCaller.AllowsDelegationTo(broader)).IsFalse();
    }
}
