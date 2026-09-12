using Parrot.Agent;

namespace Parrot.Core.Tests;

internal sealed class AgentSessionServicesTests
{
    [Test]
    public async Task Registry_returns_exact_instances_rejects_missing_and_duplicate_keys_and_isolates_registries()
    {
        var first = new AgentSessionServices();
        var second = new AgentSessionServices();
        const string firstValue = "first";
        const string secondValue = "second";
        string? missing = null;
        first.Register<IEnumerable<char>>(firstValue);
        second.Register<IEnumerable<char>>(secondValue);

        _ = await Assert.That(first.GetService<IEnumerable<char>>()).IsSameReferenceAs(firstValue);
        _ = await Assert.That(first.GetService<IEnumerable<char>>()).IsSameReferenceAs(first.GetService<IEnumerable<char>>());
        _ = await Assert.That(second.GetService<IEnumerable<char>>()).IsSameReferenceAs(secondValue);
        _ = await Assert.That(first.GetService<IEnumerable<char>>()).IsNotSameReferenceAs(second.GetService<IEnumerable<char>>());
        _ = await Assert.That(first.GetService<string>).Throws<InvalidOperationException>();
        _ = await Assert.That(() => first.GetService<object>()).Throws<InvalidOperationException>();
        _ = await Assert.That(() => first.Register<IEnumerable<char>>("duplicate")).Throws<ArgumentException>();
        _ = await Assert.That(() => first.Register<string>(missing)).Throws<ArgumentNullException>();
    }
}
