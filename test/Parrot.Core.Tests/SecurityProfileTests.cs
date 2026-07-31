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
    public async Task Read_only_composition_filters_configured_grants_and_protects_runtime_capabilities()
    {
        var profile = SecurityProfile.Compose(
            readOnly: true,
            [new("/plan", SandboxRuleAction.AllowWrite)],
            [
                new("/global", SandboxRuleAction.AllowWrite),
                new("/plan", SandboxRuleAction.DenyWrite),
                new("/grant/child", SandboxRuleAction.DenyRead),
            ],
            [new("/grant", SandboxRuleAction.AllowWrite)]);

        _ = await Assert.That(profile.AllowsWrite("/global/file")).IsFalse();
        _ = await Assert.That(profile.AllowsWrite("/plan/file")).IsTrue();
        _ = await Assert.That(profile.AllowsWrite("/grant/child/file")).IsTrue();
        _ = await Assert.That(profile.Rules.Count).IsEqualTo(3);
        _ = await Assert.That(profile.WithoutRuntimeCapabilities().AllowsWrite("/grant/file")).IsFalse();
    }

    [Test]
    public async Task Mandatory_rules_override_configuration_and_are_preserved_around_runtime_capabilities()
    {
        var mandatory = new[] { new SandboxRule("/private", SandboxRuleAction.DenyRead) };
        var profile = SecurityProfile.Compose(
            readOnly: false,
            [new SandboxRule("/private/configured", SandboxRuleAction.AllowWrite)],
            [],
            mandatory,
            []);
        var capable = profile.WithRuntimeCapability("/private/plan");

        _ = await Assert.That(profile.AllowsRead("/private/configured/file")).IsFalse();
        _ = await Assert.That(profile.AllowsWrite("/private/configured/file")).IsFalse();
        _ = await Assert.That(capable.AllowsWrite("/private/plan/file")).IsTrue();
        _ = await Assert.That(capable.AllowsRead("/private/sibling/file")).IsFalse();
        _ = await Assert.That(string.Join(',', capable.Rules.Select(rule => rule.Path)))
            .IsEqualTo("/private/configured,/private,/private/plan");
        _ = await Assert.That(capable.WithoutRuntimeCapabilities().AllowsWrite("/private/plan/file")).IsFalse();
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
    public async Task Restriction_inherits_parent_runtime_capabilities_but_applies_child_holes()
    {
        var parent = SecurityProfile.Compose(
            readOnly: true,
            [],
            [],
            [new("/runtime", SandboxRuleAction.AllowWrite)]);
        var child = SecurityProfile.Compose(
            readOnly: false,
            [
                new("/runtime/read-only", SandboxRuleAction.DenyWrite),
                new("/runtime/private", SandboxRuleAction.DenyRead),
            ],
            [],
            [new("/child-runtime", SandboxRuleAction.AllowWrite)]);
        var restricted = parent.RestrictWith(child);

        _ = await Assert.That(restricted.AllowsWrite("/runtime/file")).IsTrue();
        _ = await Assert.That(restricted.AllowsRead("/runtime/read-only/file")).IsTrue();
        _ = await Assert.That(restricted.AllowsWrite("/runtime/read-only/file")).IsFalse();
        _ = await Assert.That(restricted.AllowsRead("/runtime/private/file")).IsFalse();
        _ = await Assert.That(restricted.AllowsWrite("/child-runtime/file")).IsFalse();
        _ = await Assert.That(restricted.WithoutRuntimeCapabilities().AllowsWrite("/runtime/file")).IsFalse();
        _ = await Assert.That(restricted.RuntimeCapabilities.Count).IsEqualTo(1);
        _ = await Assert.That(restricted.RuntimeCapabilities[0])
            .IsEqualTo(new SandboxRule("/runtime", SandboxRuleAction.AllowWrite));
        _ = await Assert.That(restricted.Rules)
            .Contains(new SandboxRule("/runtime/private", SandboxRuleAction.DenyRead));
    }

    [Test]
    public async Task Restriction_narrows_runtime_capability_metadata_to_child_grant()
    {
        var parent = SecurityProfile.Compose(
            readOnly: true,
            [],
            [],
            [new("/runtime", SandboxRuleAction.AllowWrite)]);
        var child = SecurityProfile.Compose(
            readOnly: false,
            [
                new("/runtime", SandboxRuleAction.DenyRead),
                new("/runtime/public", SandboxRuleAction.AllowRead),
            ],
            [],
            []);
        var restricted = parent.RestrictWith(child);

        _ = await Assert.That(restricted.AllowsRead("/runtime/file")).IsFalse();
        _ = await Assert.That(restricted.AllowsRead("/runtime/public/file")).IsTrue();
        _ = await Assert.That(restricted.AllowsWrite("/runtime/public/file")).IsFalse();
        _ = await Assert.That(restricted.RuntimeCapabilities.Count).IsEqualTo(1);
        _ = await Assert.That(restricted.RuntimeCapabilities[0])
            .IsEqualTo(new SandboxRule("/runtime/public", SandboxRuleAction.AllowRead));
    }

    [Test]
    public async Task Delegation_allows_narrower_effective_profiles_and_rejects_runtime_escalation()
    {
        var rules = new[] { new SandboxRule("/secret", SandboxRuleAction.DenyRead) };
        var readOnlyCaller = SecurityProfile.Compose(true, rules, [], [])
            .WithRuntimeCapability("/temporary");
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
        _ = await Assert.That(matchingReadOnly.AllowsDelegationTo(readOnlyCaller)).IsFalse();
        _ = await Assert.That(readOnlyCaller.AllowsDelegationTo(matchingWritable)).IsFalse();
        _ = await Assert.That(readOnlyCaller.AllowsDelegationTo(narrower)).IsTrue();
        _ = await Assert.That(readOnlyCaller.AllowsDelegationTo(broader)).IsFalse();
        _ = await Assert.That(writableCaller.AllowsDelegationTo(matchingReadOnly)).IsTrue();
        _ = await Assert.That(writableCaller.AllowsDelegationTo(matchingWritable)).IsTrue();
        _ = await Assert.That(writableCaller.AllowsDelegationTo(narrower)).IsTrue();
        _ = await Assert.That(writableCaller.AllowsDelegationTo(broader)).IsFalse();
    }
}
