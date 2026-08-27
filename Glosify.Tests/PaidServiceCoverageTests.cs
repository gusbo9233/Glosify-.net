using Glosify.Controllers;
using Glosify.Controllers.Api;
using Glosify.Filters;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Xunit;

namespace Glosify.Tests;

public sealed class PaidServiceCoverageTests
{
    [Theory]
    [InlineData(typeof(RealtimeTranslationApiController), nameof(RealtimeTranslationApiController.CreateSession), true)]
    [InlineData(typeof(RealtimeTranslationApiController), nameof(RealtimeTranslationApiController.ReserveMinute), true)]
    [InlineData(typeof(RealtimeTranslationApiController), nameof(RealtimeTranslationApiController.BeginMinute), true)]
    [InlineData(typeof(RealtimeTranslationApiController), nameof(RealtimeTranslationApiController.Catalog), false)]
    [InlineData(typeof(RealtimeTranslationApiController), nameof(RealtimeTranslationApiController.Heartbeat), false)]
    [InlineData(typeof(RealtimeTranslationApiController), nameof(RealtimeTranslationApiController.EndSession), false)]
    [InlineData(typeof(TtsApiController), nameof(TtsApiController.Get), true)]
    [InlineData(typeof(BooksController), nameof(BooksController.Upload), false)]
    [InlineData(typeof(BooksController), nameof(BooksController.Delete), false)]
    [InlineData(typeof(BooksController), nameof(BooksController.Read), false)]
    [InlineData(typeof(BooksApiController), nameof(BooksApiController.Upload), true)]
    [InlineData(typeof(BooksApiController), nameof(BooksApiController.List), false)]
    [InlineData(typeof(BooksApiController), nameof(BooksApiController.Delete), false)]
    public void OnlyPaidOperationsCarryTheControllerGate(Type controller, string action, bool expected)
    {
        var method = Assert.Single(controller.GetMethods(), candidate => candidate.Name == action);
        Assert.Equal(expected, method.IsDefined(typeof(RequirePaidServicesAttribute), inherit: true));
    }

    [Fact]
    public void PaidServiceStatusIntentionallySupportsWebCookiesAndBearerClients()
    {
        Assert.False(typeof(ApiControllerBase).IsAssignableFrom(typeof(PaidServiceStatusApiController)));
        var authorization = Assert.Single(
            typeof(PaidServiceStatusApiController).GetCustomAttributes(
                typeof(AuthorizeAttribute),
                inherit: true).Cast<AuthorizeAttribute>());
        var schemes = authorization.AuthenticationSchemes!
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        Assert.Contains(IdentityConstants.ApplicationScheme, schemes);
        Assert.Contains(IdentityConstants.BearerScheme, schemes);
    }
}
