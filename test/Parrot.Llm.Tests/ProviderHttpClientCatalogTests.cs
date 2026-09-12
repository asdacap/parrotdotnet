using Parrot.Llm;

namespace Parrot.Core.Tests;

internal sealed class ProviderHttpClientCatalogTests
{
    [Test]
    public async Task Invalid_certificate_clients_are_scoped_reused_and_disposed(CancellationToken cancellationToken)
    {
        using var standardClient = new HttpClient();
        var catalog = new ProviderHttpClientCatalog(standardClient);

        var strict = catalog.Resolve("https://strict.example/v1", false);
        var insecure = catalog.Resolve("https://insecure.example/v1", true);
        var reused = catalog.Resolve("https://INSECURE.example:443/other", true);
        var plaintext = catalog.Resolve("http://insecure.example/v1", true);

        _ = await Assert.That(strict).IsSameReferenceAs(standardClient);
        _ = await Assert.That(plaintext).IsSameReferenceAs(standardClient);
        _ = await Assert.That(insecure).IsSameReferenceAs(reused);
        _ = await Assert.That(insecure).IsNotSameReferenceAs(standardClient);
        _ = await Assert.That(catalog.InsecureClientCount).IsEqualTo(1);
        _ = await Assert.That(ProviderHttpClientCatalog.HasAuthority(
            new Uri("https://insecure.example/v1/models"), "https://insecure.example")).IsTrue();
        _ = await Assert.That(ProviderHttpClientCatalog.HasAuthority(
            new Uri("https://other.example/v1/models"), "https://insecure.example")).IsFalse();

        catalog.Dispose();

        _ = await Assert.That(catalog.InsecureClientCount).IsEqualTo(0);
        _ = await Assert.That(async () => await insecure.GetAsync(new Uri("https://insecure.example"), cancellationToken))
            .Throws<ObjectDisposedException>();
    }
}
