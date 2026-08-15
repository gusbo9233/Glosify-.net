using System.Security.Claims;
using Glosify.Extensions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Glosify.Tests;

public sealed class BookUploadRateLimitTests
{
    [Theory]
    [InlineData("/Account/Register")]
    [InlineData("/api/auth/register")]
    public async Task SixthRegistrationWithinHour_IsRejectedPerIp(string path)
    {
        using var services = new ServiceCollection()
            .AddLogging()
            .AddGlosifyRateLimiting()
            .BuildServiceProvider();
        var limiter = services.GetRequiredService<IOptions<RateLimiterOptions>>().Value.GlobalLimiter!;
        var context = CreateContext(path, "anonymous");
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("192.0.2.1");

        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var lease = await limiter.AcquireAsync(context, 1);
            Assert.True(lease.IsAcquired);
        }

        using var rejected = await limiter.AcquireAsync(context, 1);
        Assert.False(rejected.IsAcquired);

        var otherIp = CreateContext(path, "anonymous");
        otherIp.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("192.0.2.2");
        using var accepted = await limiter.AcquireAsync(otherIp, 1);
        Assert.True(accepted.IsAcquired);
    }

    [Theory]
    [InlineData("/Books/Upload")]
    [InlineData("/api/books")]
    public async Task FourthUploadWithinWindow_IsRejectedPerUser(string path)
    {
        using var services = new ServiceCollection()
            .AddLogging()
            .AddGlosifyRateLimiting()
            .BuildServiceProvider();
        var limiter = services.GetRequiredService<IOptions<RateLimiterOptions>>().Value.GlobalLimiter!;
        var context = CreateContext(path, "user-1");

        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var lease = await limiter.AcquireAsync(context, 1);
            Assert.True(lease.IsAcquired);
        }

        using var rejected = await limiter.AcquireAsync(context, 1);
        Assert.False(rejected.IsAcquired);

        using var anotherUser = await limiter.AcquireAsync(CreateContext(path, "user-2"), 1);
        Assert.True(anotherUser.IsAcquired);
    }

    [Fact]
    public async Task EleventhCheckoutCreationWithinWindow_IsRejectedPerUser()
    {
        using var services = new ServiceCollection()
            .AddLogging()
            .AddGlosifyRateLimiting()
            .BuildServiceProvider();
        var limiter = services.GetRequiredService<IOptions<RateLimiterOptions>>().Value.GlobalLimiter!;
        var context = CreateContext("/Payments/CreateCheckoutSession", "user-1");

        for (var attempt = 0; attempt < 10; attempt++)
        {
            using var lease = await limiter.AcquireAsync(context, 1);
            Assert.True(lease.IsAcquired);
        }

        using var rejected = await limiter.AcquireAsync(context, 1);
        Assert.False(rejected.IsAcquired);

        using var anotherUser = await limiter.AcquireAsync(
            CreateContext("/Payments/CreateCheckoutSession", "user-2"),
            1);
        Assert.True(anotherUser.IsAcquired);
    }

    [Theory]
    [InlineData("/Assistant/Chats")]
    [InlineData("/api/assistant/chats")]
    [InlineData("/Quiz/11111111-1111-1111-1111-111111111111/Assistant/History")]
    public async Task SixtyFirstAssistantRequestWithinWindow_IsRejectedPerUser(string path)
    {
        using var services = new ServiceCollection()
            .AddLogging()
            .AddGlosifyRateLimiting()
            .BuildServiceProvider();
        var limiter = services.GetRequiredService<IOptions<RateLimiterOptions>>().Value.GlobalLimiter!;
        var context = CreateContext(path, "user-1");

        for (var attempt = 0; attempt < 60; attempt++)
        {
            using var lease = await limiter.AcquireAsync(context, 1);
            Assert.True(lease.IsAcquired);
        }

        using var rejected = await limiter.AcquireAsync(context, 1);
        Assert.False(rejected.IsAcquired);
    }

    [Fact]
    public async Task AssistantStaticAsset_IsNotRateLimited()
    {
        using var services = new ServiceCollection()
            .AddLogging()
            .AddGlosifyRateLimiting()
            .BuildServiceProvider();
        var limiter = services.GetRequiredService<IOptions<RateLimiterOptions>>().Value.GlobalLimiter!;
        var context = CreateContext("/js/assistant.js", "user-1");

        for (var attempt = 0; attempt < 100; attempt++)
        {
            using var lease = await limiter.AcquireAsync(context, 1);
            Assert.True(lease.IsAcquired);
        }
    }

    private static HttpContext CreateContext(string path, string userId)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = path;
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, userId)],
            "Test"));
        return context;
    }
}
