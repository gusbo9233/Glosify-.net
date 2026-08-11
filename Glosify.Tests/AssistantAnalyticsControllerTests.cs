using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Glosify.Controllers;
using Glosify.Controllers.Api;
using Glosify.Infrastructure.Api;
using Glosify.Models.Api;
using Glosify.Services.Ai.Assistant;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Glosify.Tests;

public sealed class AssistantAnalyticsControllerTests
{
    private const string UserId = "analytics-user";

    [Fact]
    public async Task WebFeedback_ForwardsAuthenticatedOwnerAndReturnsPersistedState()
    {
        var turnId = Guid.NewGuid();
        var orchestrator = new RecordingOrchestrator
        {
            Feedback = new AssistantFeedbackView(
                "down",
                ["incorrect", "too_slow"],
                "Missed the requested change.",
                DateTimeOffset.Parse("2026-08-11T12:00:00Z")),
        };
        var controller = CreateWebController(orchestrator);

        var result = await controller.SaveFeedback(
            turnId,
            new AssistantFeedbackInput("down", ["incorrect", "too_slow"], "Missed the requested change."),
            default);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Same(orchestrator.Feedback, ok.Value);
        Assert.Equal(turnId, orchestrator.TurnId);
        Assert.Equal(UserId, orchestrator.UserId);
        Assert.Equal("down", orchestrator.Rating);
        Assert.Equal(["incorrect", "too_slow"], orchestrator.ReasonCodes);
        Assert.Equal("Missed the requested change.", orchestrator.Comment);
    }

    [Fact]
    public async Task FeedbackValidation_UsesSharedProblemDetailsContract()
    {
        var orchestrator = new RecordingOrchestrator
        {
            SaveFeedbackException = new ArgumentException("Unsupported feedback reason."),
        };
        var controller = CreateMobileController(orchestrator);

        var result = await controller.SaveFeedback(
            Guid.NewGuid(),
            new AssistantFeedbackInput("down", ["made_up"], null),
            default);

        var problem = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.StatusCode);
        var details = Assert.IsType<ProblemDetails>(problem.Value);
        Assert.Equal(ApiErrorCodes.BadRequest, details.Extensions["code"]);
        Assert.Equal("Unsupported feedback reason.", details.Detail);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FeedbackOwnershipFailure_IsIndistinguishableFromMissingTurn(bool mobile)
    {
        var orchestrator = new RecordingOrchestrator
        {
            SaveFeedbackException = new UnauthorizedAccessException(),
        };

        IActionResult result = mobile
            ? await CreateMobileController(orchestrator).SaveFeedback(
                Guid.NewGuid(),
                new AssistantFeedbackInput("up", [], null),
                default)
            : await CreateWebController(orchestrator).SaveFeedback(
                Guid.NewGuid(),
                new AssistantFeedbackInput("up", [], null),
                default);

        var problem = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status404NotFound, problem.StatusCode);
        var details = Assert.IsType<ProblemDetails>(problem.Value);
        Assert.Equal(ApiErrorCodes.NotFound, details.Extensions["code"]);
        Assert.Equal("Assistant turn not found.", details.Detail);
    }

    [Fact]
    public async Task MobileClientTiming_ForwardsAuthenticatedOwnerAndReturnsNoContent()
    {
        var turnId = Guid.NewGuid();
        var orchestrator = new RecordingOrchestrator();
        var controller = CreateMobileController(orchestrator);

        var result = await controller.SaveClientMetrics(
            turnId,
            new AssistantClientMetricsInput(1234.5),
            default);

        Assert.IsType<NoContentResult>(result);
        Assert.Equal(turnId, orchestrator.TurnId);
        Assert.Equal(UserId, orchestrator.UserId);
        Assert.Equal(1234.5, orchestrator.ClientDurationMs);
    }

    [Fact]
    public async Task MobileDeleteFeedback_IsIdempotentAtControllerBoundary()
    {
        var turnId = Guid.NewGuid();
        var orchestrator = new RecordingOrchestrator();
        var controller = CreateMobileController(orchestrator);

        Assert.IsType<NoContentResult>(await controller.DeleteFeedback(turnId, default));
        Assert.IsType<NoContentResult>(await controller.DeleteFeedback(turnId, default));
        Assert.Equal(2, orchestrator.DeleteCount);
        Assert.Equal(turnId, orchestrator.TurnId);
        Assert.Equal(UserId, orchestrator.UserId);
    }

    [Fact]
    public async Task WebFeedback_RequiresAntiforgeryTokenAfterAuthorization()
    {
        using var factory = CreateAuthenticatedFactory(new RecordingOrchestrator());
        var response = await factory.CreateClient().PutAsJsonAsync(
            $"/Assistant/Turns/{Guid.NewGuid()}/Feedback",
            new AssistantFeedbackInput("up", [], null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task BearerFeedback_OptsOutOfAntiforgeryAndRequiresAuthorization()
    {
        using var anonymousFactory = new WebApplicationFactory<Program>().WithWebHostBuilder(
            builder => builder.UseEnvironment("Testing"));
        var anonymous = await anonymousFactory.CreateClient().PutAsJsonAsync(
            $"/api/assistant/turns/{Guid.NewGuid()}/feedback",
            new AssistantFeedbackInput("up", [], null));
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        var orchestrator = new RecordingOrchestrator();
        using var authenticatedFactory = CreateAuthenticatedFactory(orchestrator);
        var authenticated = await authenticatedFactory.CreateClient().PutAsJsonAsync(
            $"/api/assistant/turns/{Guid.NewGuid()}/feedback",
            new AssistantFeedbackInput("up", [], null));

        Assert.Equal(HttpStatusCode.OK, authenticated.StatusCode);
        Assert.Equal(UserId, orchestrator.UserId);
    }

    private static AssistantController CreateWebController(IAssistantOrchestrator orchestrator)
    {
        var controller = new AssistantController(orchestrator);
        SetAuthenticatedContext(controller);
        return controller;
    }

    private static AssistantApiController CreateMobileController(IAssistantOrchestrator orchestrator)
    {
        var controller = new AssistantApiController(orchestrator);
        SetAuthenticatedContext(controller);
        return controller;
    }

    private static void SetAuthenticatedContext(ControllerBase controller)
    {
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, UserId)],
            authenticationType: "test");
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) },
        };
    }

    private static WebApplicationFactory<Program> CreateAuthenticatedFactory(
        IAssistantOrchestrator orchestrator) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IAssistantOrchestrator>();
                services.AddSingleton(orchestrator);
                services.RemoveAll<IPolicyEvaluator>();
                services.AddSingleton<IPolicyEvaluator, AuthenticatedPolicyEvaluator>();
            });
        });

    private sealed class AuthenticatedPolicyEvaluator : IPolicyEvaluator
    {
        public Task<AuthenticateResult> AuthenticateAsync(
            AuthorizationPolicy policy,
            HttpContext context)
        {
            var identity = new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, UserId)],
                authenticationType: "test");
            var principal = new ClaimsPrincipal(identity);
            context.User = principal;
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(principal, "test")));
        }

        public Task<PolicyAuthorizationResult> AuthorizeAsync(
            AuthorizationPolicy policy,
            AuthenticateResult authenticationResult,
            HttpContext context,
            object? resource) => Task.FromResult(PolicyAuthorizationResult.Success());
    }

    private sealed class RecordingOrchestrator : IAssistantOrchestrator
    {
        public AssistantFeedbackView Feedback { get; set; } = new("up", [], null, DateTimeOffset.UtcNow);
        public Exception? SaveFeedbackException { get; set; }
        public Guid TurnId { get; private set; }
        public string? UserId { get; private set; }
        public string? Rating { get; private set; }
        public IReadOnlyCollection<string>? ReasonCodes { get; private set; }
        public string? Comment { get; private set; }
        public double? ClientDurationMs { get; private set; }
        public int DeleteCount { get; private set; }

        public Task<AssistantFeedbackView> SaveFeedbackAsync(
            Guid turnId,
            string userId,
            string rating,
            IReadOnlyCollection<string>? reasonCodes,
            string? comment,
            CancellationToken cancellationToken = default)
        {
            TurnId = turnId;
            UserId = userId;
            Rating = rating;
            ReasonCodes = reasonCodes;
            Comment = comment;
            return SaveFeedbackException is null
                ? Task.FromResult(Feedback)
                : Task.FromException<AssistantFeedbackView>(SaveFeedbackException);
        }

        public Task DeleteFeedbackAsync(Guid turnId, string userId, CancellationToken cancellationToken = default)
        {
            TurnId = turnId;
            UserId = userId;
            DeleteCount++;
            return Task.CompletedTask;
        }

        public Task RecordClientDurationAsync(
            Guid turnId,
            string userId,
            double clientDurationMs,
            CancellationToken cancellationToken = default)
        {
            TurnId = turnId;
            UserId = userId;
            ClientDurationMs = clientDurationMs;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<AssistantChatSummary>> ListChatsAsync(string userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AssistantChatSummary> CreateChatAsync(string userId, Guid? contextQuizId = null, CancellationToken cancellationToken = default, Guid? contextTranscriptId = null, Guid? contextBookDocumentId = null) => throw new NotSupportedException();
        public Task<AssistantChatSummary> UpdateChatAsync(Guid threadId, string userId, string? title = null, Guid? contextQuizId = null, bool updateContext = false, CancellationToken cancellationToken = default, Guid? contextTranscriptId = null, Guid? contextBookDocumentId = null) => throw new NotSupportedException();
        public Task DeleteChatAsync(Guid threadId, string userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AssistantHistory> GetChatHistoryAsync(Guid threadId, string userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AssistantTurnResponse> SendChatMessageAsync(Guid threadId, string userId, string userMessage, Guid? contextQuizId = null, string? focusedWordId = null, string? model = null, AssistantDocumentContext? documentContext = null, Guid? customQuizId = null, CancellationToken cancellationToken = default, Guid? transcriptId = null, Guid? bookDocumentId = null, AssistantTranscriptPageContext? transcriptPageContext = null) => throw new NotSupportedException();
        public Task<AssistantTurnResponse> SendMessageAsync(Guid quizId, string userId, string userMessage, string? focusedWordId = null, string? model = null, AssistantDocumentContext? documentContext = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AssistantTurnResponse> SendGlobalMessageAsync(string userId, string userMessage, string? model = null, AssistantDocumentContext? documentContext = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AssistantHistory> GetHistoryAsync(Guid quizId, string userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AssistantHistory> GetGlobalHistoryAsync(string userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AssistantApplyResult> ApplyPendingChangesAsync(Guid messageId, string userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AssistantApplyResult> ApplyGlobalPendingChangesAsync(Guid messageId, string userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RejectPendingChangesAsync(Guid messageId, string userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RejectGlobalPendingChangesAsync(Guid messageId, string userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ResetGlobalSessionAsync(string userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
