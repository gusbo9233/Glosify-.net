using System.Net;
using System.Security.Claims;
using Glosify.Data;
using Glosify.Models.Entities;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Glosify.Tests;

public sealed class GameAccessTests
{
    [Theory]
    [InlineData("admin", "stamp", false, 200)]
    [InlineData("learner", "stamp", false, 403)]
    [InlineData("admin", "old-stamp", false, 401)]
    [InlineData("admin", "stamp", true, 401)]
    [InlineData("deleted", "stamp", false, 401)]
    public async Task AccessChecksCurrentIdentityAndAdminStatus(string userId,string stamp,bool locked,int expected)
    {
        using var fixture=new Fixture();using var client=fixture.Client();
        using (var scope=fixture.App.Services.CreateScope())
        {
            var db=scope.ServiceProvider.GetRequiredService<GlosifyContext>();
            db.Users.Add(new ApplicationUser{Id="admin",UserName="admin@example.test",SecurityStamp="stamp",LockoutEnabled=true,LockoutEnd=locked?DateTimeOffset.UtcNow.AddHours(1):null});
            db.Users.Add(new ApplicationUser{Id="learner",UserName="learner@example.test",SecurityStamp="stamp"});
            await db.SaveChangesAsync();
        }
        fixture.SignIn(client,userId,stamp);
        var response=await client.GetAsync("/api/game/access");
        Assert.Equal(expected,(int)response.StatusCode);
        Assert.Null(response.Headers.Location);
        if(expected==200)Assert.Equal("{\"userId\":\"admin\"}",await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AnonymousApiReturns401WhileHandoffUsesLocalLoginReturnUrl()
    {
        using var fixture=new Fixture();using var client=fixture.Client();
        Assert.Equal(HttpStatusCode.Unauthorized,(await client.GetAsync("/api/game/access")).StatusCode);
        var response=await client.GetAsync("/sso/game");
        Assert.Equal(HttpStatusCode.Redirect,response.StatusCode);
        Assert.Contains("ReturnUrl=%2Fsso%2Fgame",response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task AdminHandoffIgnoresExternalReturnUrl()
    {
        using var fixture=new Fixture();using var client=fixture.Client();
        using(var scope=fixture.App.Services.CreateScope())
        {
            var db=scope.ServiceProvider.GetRequiredService<GlosifyContext>();
            db.Users.Add(new ApplicationUser{Id="admin",UserName="admin@example.test",SecurityStamp="stamp"});await db.SaveChangesAsync();
        }
        fixture.SignIn(client,"admin","stamp");
        Assert.Equal("https://game.globeglotter.app/",(await client.GetAsync("/sso/game?returnUrl=https://evil.example/")).Headers.Location?.AbsoluteUri);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _keys=Path.Combine(Path.GetTempPath(),"globe-auth-"+Guid.NewGuid());
        public WebApplicationFactory<Program> App {get;}
        public Fixture()
        {
            var database=Guid.NewGuid().ToString();
            App=new WebApplicationFactory<Program>().WithWebHostBuilder(builder=>
            {
                builder.UseSetting("SharedAuth:Enabled","true").UseSetting("SharedAuth:LocalKeyPath",_keys);
                builder.ConfigureAppConfiguration((_,configuration)=>configuration.AddInMemoryCollection(new Dictionary<string,string?>{["Admin:UserIds:0"]="admin"}));
                builder.ConfigureTestServices(services=>
                {
                    foreach(var item in services.Where(s=>s.ServiceType==typeof(Microsoft.Extensions.Hosting.IHostedService)).ToArray())services.Remove(item);
                    services.RemoveAll<DbContextOptions<GlosifyContext>>();services.RemoveAll<IDbContextOptionsConfiguration<GlosifyContext>>();
                    services.AddDbContext<GlosifyContext>(o=>o.UseInMemoryDatabase(database));
                });
            });
        }
        public HttpClient Client()=>App.CreateClient(new WebApplicationFactoryClientOptions{BaseAddress=new Uri("https://localhost"),AllowAutoRedirect=false,HandleCookies=false});
        public void SignIn(HttpClient client,string id,string stamp)
        {
            var options=App.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get("Identity.Application");
            var principal=new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier,id),new Claim("AspNet.Identity.SecurityStamp",stamp)],"Identity.Application"));
            var ticket=new AuthenticationTicket(principal,new AuthenticationProperties{IssuedUtc=DateTimeOffset.UtcNow,ExpiresUtc=DateTimeOffset.UtcNow.AddHours(1)},"Identity.Application");
            client.DefaultRequestHeaders.Add("Cookie",".GlobeGlotter.Auth="+options.TicketDataFormat.Protect(ticket));
        }
        public void Dispose(){App.Dispose();if(Directory.Exists(_keys))Directory.Delete(_keys,true);}
    }
}
