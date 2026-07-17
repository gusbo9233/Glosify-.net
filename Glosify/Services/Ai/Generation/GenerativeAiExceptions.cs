namespace Glosify.Services.Ai.Generation;

public sealed class GenerativeAiValidationException : ArgumentException
{
    public GenerativeAiValidationException(string message)
        : base(message)
    {
    }
}

public sealed class GenerativeAiDependencyUnavailableException : InvalidOperationException
{
    public GenerativeAiDependencyUnavailableException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

public sealed class GenerativeAiTimeoutException : TimeoutException
{
    public GenerativeAiTimeoutException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

public class GenerativeAiUpstreamException : InvalidOperationException
{
    public GenerativeAiUpstreamException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

public sealed class GenerativeAiStructuredOutputException : GenerativeAiUpstreamException
{
    public GenerativeAiStructuredOutputException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
