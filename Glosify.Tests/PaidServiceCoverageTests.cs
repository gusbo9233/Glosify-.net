using Glosify.Controllers;
using Glosify.Controllers.Api;
using Glosify.Controllers.Classrooms;
using Glosify.Filters;
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
    [InlineData(typeof(SpeakingApiController), nameof(SpeakingApiController.SpeechToken), true)]
    [InlineData(typeof(SpeakingApiController), nameof(SpeakingApiController.CreateSession), true)]
    [InlineData(typeof(SpeakingApiController), nameof(SpeakingApiController.SendTurn), true)]
    [InlineData(typeof(SpeakingApiController), nameof(SpeakingApiController.DeleteSession), false)]
    [InlineData(typeof(TtsApiController), nameof(TtsApiController.Get), true)]
    [InlineData(typeof(BooksController), nameof(BooksController.Upload), true)]
    [InlineData(typeof(BooksController), nameof(BooksController.Delete), false)]
    [InlineData(typeof(BooksController), nameof(BooksController.Read), false)]
    [InlineData(typeof(BooksApiController), nameof(BooksApiController.Upload), true)]
    [InlineData(typeof(BooksApiController), nameof(BooksApiController.List), false)]
    [InlineData(typeof(BooksApiController), nameof(BooksApiController.Delete), false)]
    [InlineData(typeof(ClassroomCallController), nameof(ClassroomCallController.CallToken), true)]
    [InlineData(typeof(ClassroomCallController), nameof(ClassroomCallController.Call), false)]
    public void OnlyPaidOperationsCarryTheControllerGate(Type controller, string action, bool expected)
    {
        var method = Assert.Single(controller.GetMethods(), candidate => candidate.Name == action);
        Assert.Equal(expected, method.IsDefined(typeof(RequirePaidServicesAttribute), inherit: true));
    }
}
