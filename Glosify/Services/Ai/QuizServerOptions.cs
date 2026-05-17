namespace Glosify.Services;

public sealed class QuizServerOptions
{
    public string BaseUrl { get; set; } = string.Empty;
    public int TimeoutSeconds { get; set; } = 180;
}
