using System.Net;
using Glosify.Services.Abuse;
using Xunit;

namespace Glosify.Tests;

public sealed class SignupAdmissionTests
{
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
