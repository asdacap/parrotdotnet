using System.Net;
using Parrot.Web;

namespace Parrot.Core.Tests;

internal sealed class WebAddressPolicyTests
{
    [Test]
    [Arguments("0.0.0.1", false)]
    [Arguments("10.0.0.1", false)]
    [Arguments("100.64.0.1", false)]
    [Arguments("127.0.0.1", false)]
    [Arguments("169.254.0.1", false)]
    [Arguments("172.16.0.1", false)]
    [Arguments("192.0.2.1", false)]
    [Arguments("192.168.0.1", false)]
    [Arguments("198.18.0.1", false)]
    [Arguments("198.51.100.1", false)]
    [Arguments("203.0.113.1", false)]
    [Arguments("240.0.0.1", false)]
    [Arguments("64:ff9b:1::1", false)]
    [Arguments("100::1", false)]
    [Arguments("2001:db8::1", false)]
    [Arguments("8.8.8.8", true)]
    [Arguments("1.1.1.1", true)]
    [Arguments("2606:4700:4700::1111", true)]
    public async Task Public_policy_accepts_only_public_addresses(string address, bool expected)
    {
        IWebAddressPolicy policy = new PublicWebAddressPolicy();
        _ = await Assert.That(policy.Allows(IPAddress.Parse(address))).IsEqualTo(expected);
    }

    [Test]
    public async Task Host_and_private_opt_in_rules_are_applied()
    {
        IWebAddressPolicy publicPolicy = new PublicWebAddressPolicy();
        IWebAddressPolicy privatePolicy = new PrivateWebAddressPolicy();

        _ = await Assert.That(publicPolicy.AllowsHost("localhost")).IsFalse();
        _ = await Assert.That(publicPolicy.AllowsHost("api.localhost")).IsFalse();
        _ = await Assert.That(publicPolicy.AllowsHost("example.com")).IsTrue();
        _ = await Assert.That(privatePolicy.AllowsHost("localhost")).IsTrue();
        _ = await Assert.That(privatePolicy.Allows(IPAddress.Loopback)).IsTrue();
        _ = await Assert.That(privatePolicy.Allows(IPAddress.Any)).IsFalse();
        _ = await Assert.That(privatePolicy.Allows(IPAddress.Parse("224.0.0.1"))).IsFalse();
    }
}
