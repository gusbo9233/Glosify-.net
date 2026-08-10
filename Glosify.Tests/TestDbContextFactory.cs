using Glosify.Data;
using Microsoft.EntityFrameworkCore;

namespace Glosify.Tests;

internal sealed class TestDbContextFactory : IDbContextFactory<GlosifyContext>
{
    private readonly DbContextOptions<GlosifyContext> _options;

    public TestDbContextFactory(GlosifyContext context)
    {
        _options = context is FactoryBackedGlosifyContext testContext
            ? testContext.Options
            : throw new ArgumentException("The test context must expose its options.", nameof(context));
    }

    public GlosifyContext CreateDbContext() => new(_options);
}

internal sealed class FactoryBackedGlosifyContext(
    DbContextOptions<GlosifyContext> options) : GlosifyContext(options)
{
    public DbContextOptions<GlosifyContext> Options { get; } = options;
}
