using Microsoft.Data.SqlClient;

namespace Glosify.Services;

public static class ServiceWarmupMessage
{
    public const string Database = "The database is waking up. This can take a minute after it has been idle. Please try again shortly.";
    public const string QuizServer = "The quiz server is waking up. This can take a minute after it has been idle. Please try generating again shortly.";
    public const string Dependencies = "Glosify is waiting for its database and background services to wake up. Please try again in a minute.";

    public static bool IsDatabaseWarmupFailure(Exception exception)
    {
        return exception is TimeoutException
            || exception is SqlException
            || exception.GetBaseException() is SqlException
            || exception.InnerException is not null && IsDatabaseWarmupFailure(exception.InnerException);
    }

    public static bool IsQuizServerWarmupFailure(Exception exception)
    {
        return exception is HttpRequestException
            || exception is TaskCanceledException
            || exception.GetBaseException() is HttpRequestException
            || exception.InnerException is not null && IsQuizServerWarmupFailure(exception.InnerException);
    }
}
