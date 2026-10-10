using Glosify.Controllers;
using Glosify.Data;
using Glosify.Models.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Glosify.Tests;

public sealed class GameUsageSqlTests
{
    [SqlServerFact]
    public Task DashboardQueriesRunOnSqlServerForEmptyAndBoundedActivity() => SqlServerTestDatabase.RunAsync("game_usage", async db =>
    {
        var from = DateTimeOffset.UtcNow.AddHours(-1); var to=from.AddMinutes(30);
        var controller = new GameUsageAdminController(db);
        Assert.IsType<ViewResult>(await controller.Index(from,to,null,null,null,null,null,default));
        Assert.Equal(0m,controller.ViewData["EstimatedUsd"]); Assert.Equal(0,controller.ViewData["Sessions"]);
        var session=Guid.NewGuid().ToString("N");
        db.AddRange(new GameUsageEvent {Id=Guid.NewGuid(),UserId="admin",SessionId=session,Provider="game",Operation="session.active",StartedAt=from.AddMinutes(-1),AudioSeconds=60},
            new GameUsageEvent {Id=Guid.NewGuid(),UserId="admin",SessionId=session,Provider="game",Operation="session.active",StartedAt=from.AddMinutes(1),AudioSeconds=150},
            new GameUsageEvent {Id=Guid.NewGuid(),UserId="admin",SessionId=session,Provider="openai",Model="gpt-6-luna",Operation="starting-location",StartedAt=from.AddMinutes(2),EstimatedUsd=.001m});
        await db.SaveChangesAsync();
        Assert.IsType<ViewResult>(await controller.Index(from,to,"admin","openai",null,null,session,default));
        Assert.Equal(.001m,controller.ViewData["EstimatedUsd"]); Assert.Equal(90m,controller.ViewData["ActiveSeconds"]);
        var row=Assert.Single(Assert.IsType<List<GameUsageBreakdown>>(controller.ViewData["Breakdown"]));
        Assert.Equal("starting-location",row.Operation); Assert.Equal(1,row.Calls);
    });

    [SqlServerFact]
    public Task StaleSessionTotalsCannotOverwriteANewerSnapshot() => SqlServerTestDatabase.RunAsync("game_sessions", async db =>
    {
        var id=Guid.NewGuid().ToString("N"); var start=DateTimeOffset.UtcNow;
        db.Add(new GamePlaySession {Id=id,UserId="admin",StartedAt=start,LastSeenAt=start,ActiveSeconds=60});await db.SaveChangesAsync();
        var options=new DbContextOptionsBuilder<GlosifyContext>().UseSqlServer(db.Database.GetConnectionString()).Options;
        await using var newer=new GlosifyContext(options);await using var stale=new GlosifyContext(options);
        var a=await newer.Set<GamePlaySession>().SingleAsync();var b=await stale.Set<GamePlaySession>().SingleAsync();
        a.ActiveSeconds=150;a.LastSeenAt=start.AddMinutes(2);await newer.SaveChangesAsync();
        b.ActiveSeconds=100;b.LastSeenAt=start.AddMinutes(1);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(()=>stale.SaveChangesAsync());
        var saved=await db.Set<GamePlaySession>().AsNoTracking().SingleAsync();Assert.Equal(150,saved.ActiveSeconds);Assert.Equal(a.LastSeenAt,saved.LastSeenAt);
    });
}
