using Azure.Core;
using Glosify.Services.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace Glosify.Tests;

public sealed class GlosifyBlobServiceClientTests
{
    [Theory]
    [InlineData("http://storage.example.test")]
    [InlineData("ftp://storage.example.test")]
    public void ProductionRejectsNonHttpsServiceUris(string serviceUri)
    {
        var exception = Assert.Throws<InvalidOperationException>(() => new GlosifyBlobServiceClient(
            Options.Create(new BlobStorageOptions { ServiceUri = serviceUri }),
            new StubTokenCredential(),
            new StubHostEnvironment(Environments.Production)));

        Assert.Contains("must use HTTPS", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionRejectsDevelopmentStorageConnectionStrings()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => new GlosifyBlobServiceClient(
            Options.Create(new BlobStorageOptions { ConnectionString = "UseDevelopmentStorage=true" }),
            new StubTokenCredential(),
            new StubHostEnvironment(Environments.Production)));

        Assert.Contains("must use HTTPS", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DevelopmentAllowsTheLoopbackStorageEmulator()
    {
        _ = new GlosifyBlobServiceClient(
            Options.Create(new BlobStorageOptions { ConnectionString = "UseDevelopmentStorage=true" }),
            new StubTokenCredential(),
            new StubHostEnvironment(Environments.Development));
    }

    [Fact]
    public void ProductionAllowsHttpsServiceUris()
    {
        _ = new GlosifyBlobServiceClient(
            Options.Create(new BlobStorageOptions { ServiceUri = "https://storage.example.test" }),
            new StubTokenCredential(),
            new StubHostEnvironment(Environments.Production));
    }

    private sealed class StubTokenCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new("unused", DateTimeOffset.MaxValue);

        public override ValueTask<AccessToken> GetTokenAsync(
            TokenRequestContext requestContext,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new AccessToken("unused", DateTimeOffset.MaxValue));
    }
}
