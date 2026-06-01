using Microsoft.Data.SqlClient;

namespace Glosify.Services;

public static class ServiceWarmupMessage
{
    public const string Database = "The database is waking up. This can take a minute after it has been idle. Please try again shortly.";
    public const string LlmAssistant = "The AI assistant is taking longer than usual. Please try again in a moment.";
    public const string Dependencies = "Glosify is waiting for its database and background services to wake up. Please try again in a minute.";

    public static bool IsDatabaseWarmupFailure(Exception exception)
    {
        return exception is TimeoutException
            || exception is SqlException sqlException && IsTransientSqlWarmupFailure(sqlException)
            || exception.GetBaseException() is SqlException baseSqlException && IsTransientSqlWarmupFailure(baseSqlException)
            || exception.InnerException is not null && IsDatabaseWarmupFailure(exception.InnerException);
    }

    public static bool IsLlmWarmupFailure(Exception exception)
    {
        return exception is HttpRequestException
            || exception is TaskCanceledException
            || exception.GetBaseException() is HttpRequestException
            || exception.InnerException is not null && IsLlmWarmupFailure(exception.InnerException);
    }

    private static bool IsTransientSqlWarmupFailure(SqlException exception)
    {
        foreach (SqlError error in exception.Errors)
        {
            if (IsTransientSqlWarmupErrorNumber(error.Number))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsTransientSqlWarmupErrorNumber(int number)
    {
        return number is
            -2 // SQL command timeout
            or 64 // network name no longer available
            or 233 // connection initialization failure
            or 10053 // connection aborted
            or 10054 // connection reset
            or 10060 // connection timed out
            or 10928 // Azure SQL resource limit
            or 10929 // Azure SQL resource limit
            or 40143 // transient Azure SQL service error
            or 40197 // transient Azure SQL service error
            or 40501 // Azure SQL service busy/throttling
            or 40613 // Azure SQL database unavailable, often serverless resume
            or 49918 // too many operations
            or 49919 // too many create/update operations
            or 49920; // too many operations
    }
}
