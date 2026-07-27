using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Glosify.Data;

/// <summary>
/// Keeps EF design-time operations independent from web-host startup and external services.
/// </summary>
public sealed class GlosifyContextFactory : IDesignTimeDbContextFactory<GlosifyContext>
{
    public GlosifyContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable(
            "ConnectionStrings__DefaultConnection");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            connectionString =
                "Server=(localdb)\\mssqllocaldb;Database=GlosifyDesignTime;Trusted_Connection=True;TrustServerCertificate=True";
        }

        var options = new DbContextOptionsBuilder<GlosifyContext>()
            .UseSqlServer(connectionString)
            .Options;
        return new GlosifyContext(options);
    }
}
