using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace Glosify.Data;

/// <summary>
/// Keeps EF design-time operations independent from web-host startup and external services.
/// Configuration is read directly rather than through the host, so `dotnet ef` never has to
/// reach OpenAI, Blob Storage, or anything else the app resolves at boot.
/// </summary>
public sealed class GlosifyContextFactory : IDesignTimeDbContextFactory<GlosifyContext>
{
    public GlosifyContext CreateDbContext(string[] args)
    {
        // User secrets carry the local development connection string, so `dotnet ef
        // database update` works with no ceremony. The environment variable is layered
        // last and therefore still wins, which is what CI and any deliberate run against
        // another database rely on.
        var configuration = new ConfigurationBuilder()
            .AddUserSecrets<GlosifyContextFactory>(optional: true)
            .AddEnvironmentVariables()
            .Build();

        var connectionString = configuration.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "No 'DefaultConnection' connection string was found for design-time use. "
                + "Start the local database with `docker compose -f docker-compose.dev.yml up -d` "
                + "and set it with `dotnet user-secrets set \"ConnectionStrings:DefaultConnection\" "
                + "\"<value>\" --project Glosify`, or pass ConnectionStrings__DefaultConnection "
                + "in the environment. See the README.");
        }

        var options = new DbContextOptionsBuilder<GlosifyContext>()
            .UseSqlServer(connectionString)
            .Options;
        return new GlosifyContext(options);
    }
}
