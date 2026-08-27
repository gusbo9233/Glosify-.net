using System.Runtime.CompilerServices;

namespace Glosify.Tests;

internal static class TestEnvironment
{
    internal static string? ConfiguredSqlServerConnectionString { get; private set; }

    /// <summary>
    /// Points the test host's database at nothing reachable before any host is built.
    /// </summary>
    /// <remarks>
    /// <see cref="Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory{T}"/> boots in the
    /// Development environment, which loads Glosify/appsettings.Development.json — and that
    /// file holds the live Azure SQL connection string. Environment variables outrank
    /// appsettings files in the default configuration order, so setting this here keeps every
    /// test off production no matter what it does with its DbContext. It also gives CI a
    /// connection string, without which Program.cs refuses to build the host at all.
    /// An existing value is left alone, so pointing tests at a real database stays possible
    /// by setting the variable deliberately.
    /// </remarks>
    [ModuleInitializer]
    internal static void PinDatabaseAwayFromProduction()
    {
        const string key = "ConnectionStrings__DefaultConnection";
        var configuredConnection = Environment.GetEnvironmentVariable(key);
        ConfiguredSqlServerConnectionString = string.IsNullOrWhiteSpace(configuredConnection)
            ? null
            : configuredConnection;

        if (ConfiguredSqlServerConnectionString is null)
        {
            Environment.SetEnvironmentVariable(
                key,
                "Server=localhost,1433;Initial Catalog=glosify-tests;Encrypt=False;Integrated Security=true;");
        }
    }
}
