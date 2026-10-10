using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Glosify.Data;
using Glosify.Models.Entities;
using Glosify.Services.Game;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Glosify.Tests;

public sealed class GameIntegrationTests
{
    private static async Task<Guid> Seed(GameAccessTests.Fixture fixture)
    {
        using var scope = fixture.App.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<GlosifyContext>();
        db.AccountingBypass = true;
        db.Users.Add(new() { Id = "admin", UserName = "admin@example.test", SecurityStamp = "stamp" });
        db.Users.Add(new() { Id = "learner", UserName = "learner@example.test", SecurityStamp = "stamp" });
        var id = Guid.NewGuid(); db.Quizzes.Add(new() { Id = id, UserId = "admin", Name = "At the café", SourceLanguage = "English", TargetLanguage = "Swedish" });
        db.Quizzes.Add(new() { Id = Guid.NewGuid(), UserId = "learner", Name = "Private", SourceLanguage = "English", TargetLanguage = "French" });
        db.Words.Add(new() { Id = "word", QuizId = id, Lemma = "kaffe", Translation = "coffee" });
        db.QuizSentences.Add(new() { Id = Guid.NewGuid(), QuizId = id, Text = "En kaffe, tack", Translation = "A coffee, please" });
        await db.SaveChangesAsync(); return id;
    }
    [Fact] public async Task QuizApiOnlyExposesOwnedContentAndLaunchCarriesOwnedId()
    {
        using var fixture = new GameAccessTests.Fixture(); using var client = fixture.Client(); var id = await Seed(fixture); fixture.SignIn(client, "admin", "stamp");
        var library = await client.GetFromJsonAsync<JsonElement>("/api/game/quizzes");
        Assert.Single(library.GetProperty("quizzes").EnumerateArray());
        var content = await client.GetFromJsonAsync<JsonElement>($"/api/game/quizzes/{id}");
        Assert.Equal("kaffe", content.GetProperty("words")[0].GetProperty("text").GetString());
        Assert.Equal("En kaffe, tack", content.GetProperty("sentences")[0].GetProperty("text").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/game/quizzes/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal($"https://game.globeglotter.app/?quizId={id:D}", (await client.GetAsync($"/sso/game?quizId={id}")).Headers.Location?.AbsoluteUri);
        using var learner = fixture.Client(); fixture.SignIn(learner, "learner", "stamp");
        Assert.Equal(HttpStatusCode.Forbidden, (await learner.GetAsync("/api/game/quizzes")).StatusCode);
    }
    private static JsonElement Character(Guid? quizId = null) => JsonSerializer.SerializeToElement(new
    {
        version = 1, profile = new { version = 1, name = "Role play", age = "25" },
        appearance = new { version = 1, body = "straight", outfit = "jacket" }, gameLanguage = "sv", quizId
    });
    [Fact] public async Task ProfileRequiresTrustedServerAndDetectsConflictingSaves()
    {
        using var fixture = new GameAccessTests.Fixture(); using var client = fixture.Client(); var id = await Seed(fixture); fixture.SignIn(client,"admin","stamp");
        var body = new { version = (Guid?)null, character = Character(id) };
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync("/api/game/profile", body)).StatusCode);
        client.DefaultRequestHeaders.Add("X-Game-Service-Key", new string('x',40));
        var saved = await client.PutAsJsonAsync("/api/game/profile", body); Assert.Equal(HttpStatusCode.OK,saved.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync("/api/game/profile", body)).StatusCode);
        var profile = await client.GetFromJsonAsync<JsonElement>("/api/game/profile");
        Assert.Equal("sv",profile.GetProperty("character").GetProperty("gameLanguage").GetString());
        Assert.NotEqual(Guid.Empty,profile.GetProperty("version").GetGuid());
        Assert.Equal(HttpStatusCode.NotFound,(await client.PutAsJsonAsync("/api/game/profile",new {version=profile.GetProperty("version").GetGuid(),character=Character(Guid.NewGuid())})).StatusCode);
    }
    [Fact] public async Task IngestionIsServerOnlyIdempotentAndPendingCannotOverwriteCompletion()
    {
        using var fixture = new GameAccessTests.Fixture(); using var client = fixture.Client(); await Seed(fixture);
        var usage = new GameUsageEvent {Id=Guid.NewGuid(),UserId="admin",SessionId=Guid.NewGuid().ToString("N"),Provider="openai",Model="gpt-6-luna",Endpoint="decisions",Operation="starting-location",Outcome="pending",StartedAt=DateTimeOffset.UtcNow};
        Assert.Equal(HttpStatusCode.Forbidden,(await client.PostAsJsonAsync("/api/game/internal/usage",usage)).StatusCode);
        client.DefaultRequestHeaders.Add("X-Game-Service-Key",new string('x',40));
        Assert.Equal(HttpStatusCode.NoContent,(await client.PostAsJsonAsync("/api/game/internal/usage",usage)).StatusCode);
        usage.Outcome="completed";usage.Measurement="actual";usage.InputTokens=100;
        Assert.Equal(HttpStatusCode.NoContent,(await client.PostAsJsonAsync("/api/game/internal/usage",usage)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,(await client.PostAsJsonAsync("/api/game/internal/usage",usage)).StatusCode);
        usage.Outcome="pending";usage.InputTokens=null;
        await client.PostAsJsonAsync("/api/game/internal/usage",usage);
        using var scope=fixture.App.Services.CreateScope();var db=scope.ServiceProvider.GetRequiredService<GlosifyContext>();
        var row=Assert.Single(await db.Set<GameUsageEvent>().ToListAsync());Assert.Equal(100,row.InputTokens);Assert.Equal("completed",row.Outcome);
        Assert.Empty(await db.AiCreditTransactions.ToListAsync());
    }
    [Fact] public async Task UsageDashboardFiltersActivityAndRejectsNonAdmins()
    {
        using var fixture = new GameAccessTests.Fixture(); using var client = fixture.Client(); await Seed(fixture);
        var start = DateTimeOffset.UtcNow.AddHours(-1); var session = Guid.NewGuid().ToString("N");
        using (var scope = fixture.App.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GlosifyContext>(); db.AccountingBypass = true;
            db.AddRange(new GameUsageEvent { Id=Guid.NewGuid(), UserId="admin", SessionId=session, Provider="game", Operation="session.active", AudioSeconds=60, StartedAt=start.AddMinutes(-1) },
                new GameUsageEvent { Id=Guid.NewGuid(), UserId="admin", SessionId=session, Provider="game", Operation="session.active", AudioSeconds=150, StartedAt=start.AddMinutes(1) },
                new GameUsageEvent { Id=Guid.NewGuid(), UserId="admin", SessionId=session, Provider="openai", Model="gpt-6-luna", Operation="starting-location", EstimatedUsd=.001m, StartedAt=start.AddMinutes(1) });
            await db.SaveChangesAsync();
        }
        fixture.SignIn(client,"learner","stamp");
        var denied = await client.GetAsync("/Admin/GameUsage");
        Assert.Equal(HttpStatusCode.Redirect,denied.StatusCode); Assert.Equal("/Home/Error",denied.Headers.Location!.AbsolutePath);
        fixture.SignIn(client,"admin","stamp");
        var response = await client.GetAsync("/Admin/GameUsage?from=" + Uri.EscapeDataString(start.ToString("O")));
        Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("90 active seconds",html); Assert.Contains("$0.001000",html); Assert.Contains("starting-location",html);
    }
    [Fact] public void PricingSeparatesDecisionsFromResponsesAndPreservesUnknowns()
    {
        var start=DateTimeOffset.UtcNow;
        var rates=new[]{new GameUsageRate{Provider="openai",Model="gpt-6-luna",Endpoint="decisions",InputPerMillion=.1m},new GameUsageRate{Provider="openai",Model="gpt-6-luna",Endpoint="responses",InputPerMillion=.2m,CachedInputPerMillion=.02m,OutputPerMillion=1m}};
        var e=new GameUsageEvent{Provider="openai",Model="gpt-6-luna",Endpoint="decisions",StartedAt=start,Measurement="actual",InputTokens=1000000,CachedInputTokens=500000,OutputTokens=9000000};
        GameUsagePricing.Apply(e,rates);Assert.Equal(.1m,e.EstimatedUsd);
        e.Endpoint="responses";GameUsagePricing.Apply(e,rates);Assert.Equal(9.11m,e.EstimatedUsd);Assert.NotNull(e.RateJson);
        e.Measurement="unknown";GameUsagePricing.Apply(e,rates);Assert.Null(e.EstimatedUsd);
        e.Measurement="actual";e.Model="unpriced";GameUsagePricing.Apply(e,rates);Assert.Null(e.EstimatedUsd);
    }
    [Fact] public void InvalidProfileVersionsAndAgeAreRejectedWithoutExceptions()
    {
        Assert.False(GameProfileValidation.IsValid(JsonSerializer.SerializeToElement(new {version="1"})));
        Assert.False(GameProfileValidation.IsValid(JsonSerializer.SerializeToElement(new {version=1,profile=new {version="bad"}})));
        Assert.True(GameProfileValidation.IsValid(Character()));
    }
}
