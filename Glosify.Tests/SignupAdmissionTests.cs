using System.Net;
using Glosify.Services.Abuse;
using Xunit;

namespace Glosify.Tests;

public sealed class SignupAdmissionTests
{
    internal static readonly string HashKey = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("test-only-signup-key-32-bytes-long"));

    [Fact]
    public async Task MissingServerKeyClosesNewAdmissionWithoutConsumingBuckets()
    {
        var settings = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<Glosify.Data.GlosifyContext>();
        Microsoft.EntityFrameworkCore.InMemoryDbContextOptionsExtensions.UseInMemoryDatabase(settings, Guid.NewGuid().ToString());
        await using var db = new Glosify.Data.GlosifyContext(settings.Options);
        var service = new SignupAdmissionService(db, new Microsoft.AspNetCore.Http.HttpContextAccessor(),
            Microsoft.Extensions.Options.Options.Create(new AbuseOptions()), TimeProvider.System);
        await Assert.ThrowsAsync<SignupLimitException>(() => service.AdmitAsync());
        Assert.Empty(db.Set<SignupBucket>());
    }

    [Fact]
    public void AddressBucketsDependOnServerSecretAndTimeWindow()
    {
        var ip = IPAddress.Parse("192.0.2.1");
        var hash = SignupAdmissionService.HashAddress("window-1", ip, HashKey);
        Assert.Equal(hash, SignupAdmissionService.HashAddress("window-1", IPAddress.Parse("::ffff:192.0.2.1"), HashKey));
        Assert.NotEqual(hash, SignupAdmissionService.HashAddress("window-2", ip, HashKey));
        Assert.NotEqual(hash, SignupAdmissionService.HashAddress("window-1", ip, Convert.ToBase64String(new byte[32])));
        Assert.False(SignupAdmissionService.HasValidHashKey(null));
        Assert.False(SignupAdmissionService.HasValidHashKey(Convert.ToBase64String(new byte[16])));
        Assert.True(SignupAdmissionService.HasValidHashKey(HashKey));
    }

    [Fact]
    public void IPv6PrivacyAddressesSharePrefix_AndMappedIPv4CannotBypassLimit()
    {
        Assert.Equal(SignupAdmissionService.NormalizeAddress(IPAddress.Parse("2001:db8:1234:abcd::1")),
            SignupAdmissionService.NormalizeAddress(IPAddress.Parse("2001:db8:1234:abcd:5678::2")));
        Assert.NotEqual(SignupAdmissionService.NormalizeAddress(IPAddress.Parse("2001:db8:1234:abcd::1")),
            SignupAdmissionService.NormalizeAddress(IPAddress.Parse("2001:db8:1234:abce::1")));
        Assert.Equal(SignupAdmissionService.NormalizeAddress(IPAddress.Parse("192.0.2.1")),
            SignupAdmissionService.NormalizeAddress(IPAddress.Parse("::ffff:192.0.2.1")));
    }
}
