using System.Runtime.ExceptionServices;
using Glosify.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Glosify.Tests;

internal sealed class SqlServerFactAttribute : FactAttribute
{
    public SqlServerFactAttribute()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("RUN_SQLSERVER_TESTS"),
                "true",
                StringComparison.OrdinalIgnoreCase))
        {
            Skip = "Set RUN_SQLSERVER_TESTS=true to run SQL Server integration tests.";
        }
    }
}

internal sealed class SqlServerTestDatabase : IAsyncDisposable
{
    private bool _created;

    private SqlServerTestDatabase(GlosifyContext context)
    {
        Context = context;
    }

    public GlosifyContext Context { get; }

    public static async Task RunAsync(
        string databasePurpose,
        Func<GlosifyContext, Task> test)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePurpose);
        ArgumentNullException.ThrowIfNull(test);

        var database = await CreateAsync(databasePurpose);
        Exception? testFailure = null;
        try
        {
            await test(database.Context);
        }
        catch (Exception exception)
        {
            testFailure = exception;
        }

        Exception? cleanupFailure = null;
        try
        {
            await database.DisposeAsync();
        }
        catch (Exception exception)
        {
            cleanupFailure = exception;
        }

        if (testFailure is not null && cleanupFailure is not null)
        {
            throw new AggregateException(
                "The SQL Server test and its database cleanup both failed.",
                testFailure,
                cleanupFailure);
        }
        if (testFailure is not null)
        {
            ExceptionDispatchInfo.Capture(testFailure).Throw();
        }
        if (cleanupFailure is not null)
        {
            ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
        }
    }

    public async ValueTask DisposeAsync()
    {
        Exception? deletionFailure = null;
        if (_created)
        {
            try
            {
                await Context.Database.EnsureDeletedAsync();
            }
            catch (Exception exception)
            {
                deletionFailure = exception;
            }
        }

        Exception? disposalFailure = null;
        try
        {
            await Context.DisposeAsync();
        }
        catch (Exception exception)
        {
            disposalFailure = exception;
        }

        if (deletionFailure is not null && disposalFailure is not null)
        {
            throw new AggregateException(
                "Deleting and disposing the SQL Server test database both failed.",
                deletionFailure,
                disposalFailure);
        }
        if (deletionFailure is not null)
        {
            ExceptionDispatchInfo.Capture(deletionFailure).Throw();
        }
        if (disposalFailure is not null)
        {
            ExceptionDispatchInfo.Capture(disposalFailure).Throw();
        }
    }

    private static async Task<SqlServerTestDatabase> CreateAsync(string databasePurpose)
    {
        var configuredConnection = TestEnvironment.ConfiguredSqlServerConnectionString
            ?? throw new InvalidOperationException(
                "RUN_SQLSERVER_TESTS=true requires ConnectionStrings__DefaultConnection to be explicitly configured.");

        SqlConnectionStringBuilder connection;
        try
        {
            connection = new SqlConnectionStringBuilder(configuredConnection)
            {
                InitialCatalog = $"glosify-{databasePurpose}-{Guid.NewGuid():N}",
            };
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException(
                "ConnectionStrings__DefaultConnection is not a valid SQL Server connection string.",
                exception);
        }

        var options = new DbContextOptionsBuilder<GlosifyContext>()
            .UseSqlServer(connection.ConnectionString)
            .Options;
        var database = new SqlServerTestDatabase(new GlosifyContext(options));
        try
        {
            await database.Context.Database.EnsureCreatedAsync();
            database._created = true;
            return database;
        }
        catch (Exception creationFailure)
        {
            try
            {
                await database.Context.DisposeAsync();
            }
            catch (Exception disposalFailure)
            {
                throw new AggregateException(
                    "Creating and disposing the SQL Server test database both failed.",
                    creationFailure,
                    disposalFailure);
            }

            ExceptionDispatchInfo.Capture(creationFailure).Throw();
            throw;
        }
    }
}
