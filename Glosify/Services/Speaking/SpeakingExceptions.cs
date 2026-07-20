namespace Glosify.Services.Speaking;

public sealed class SpeakingSessionNotFoundException()
    : InvalidOperationException("Speaking session was not found.");

public sealed class SpeakingSessionExpiredException()
    : InvalidOperationException("This speaking session has expired. Start a new one to continue.");

public sealed class SpeakingSessionInvalidatedException(Exception innerException)
    : InvalidOperationException(
        "This speaking session could not be continued safely. Start a new one to continue.",
        innerException);

public sealed class SpeakingSessionBusyException()
    : InvalidOperationException("Wait for the current avatar reply before sending another message.");

public sealed class SpeakingSessionLimitException(int limit)
    : InvalidOperationException($"You can have at most {limit} active speaking sessions.");

public sealed class SpeakingValidationException(string message)
    : ArgumentException(message);

public sealed class SpeakingDependencyUnavailableException : InvalidOperationException
{
    public SpeakingDependencyUnavailableException(string message)
        : base(message)
    {
    }

    public SpeakingDependencyUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class SpeakingUpstreamException : InvalidOperationException
{
    public SpeakingUpstreamException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
