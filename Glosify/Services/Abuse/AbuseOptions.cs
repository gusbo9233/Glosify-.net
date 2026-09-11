using Microsoft.Extensions.Options;

namespace Glosify.Services.Abuse;

public sealed class AbuseOptions
{
    public int Quizzes { get; set; } = 1_000;
    public int QuizItems { get; set; } = 50_000;
    public int ItemsPerQuiz { get; set; } = 1_000;
    public int Books { get; set; } = 50;
    public long MaxPdfBytes { get; set; } = 25L * 1024 * 1024;
    public int Collections { get; set; } = 200;
    public int Chats { get; set; } = 100;
    public int Transcripts { get; set; } = 100;
    public int Translations { get; set; } = 10_000;
    public int TranslationSessions { get; set; } = 100;
    public long PdfBytes { get; set; } = 500L * 1024 * 1024;
    public long ContentBytes { get; set; } = 100L * 1024 * 1024;
    public long SitePdfBytes { get; set; } = 10L * 1024 * 1024 * 1024;
    public long SiteContentBytes { get; set; } = 1024L * 1024 * 1024;
    public bool SignupsEnabled { get; set; } = true;
    public int SignupsPerIpHour { get; set; } = 5;
    public int SignupsPerIpDay { get; set; } = 10;
    public int SignupsPerDay { get; set; } = 200;

    public long Limit(string resource, bool site = false) => (resource, site) switch
    {
        ("content_bytes", true) => SiteContentBytes,
        ("pdf_bytes", true) => SitePdfBytes,
        (_, true) => long.MaxValue,
        ("quizzes", _) => Quizzes, ("quiz_items", _) => QuizItems,
        ("books", _) => Books, ("collections", _) => Collections,
        ("chats", _) => Chats, ("transcripts", _) => Transcripts,
        ("translations", _) => Translations, ("translation_sessions", _) => TranslationSessions,
        ("content_bytes", _) => ContentBytes, ("pdf_bytes", _) => PdfBytes,
        _ when resource.StartsWith("quiz:", StringComparison.Ordinal) => ItemsPerQuiz,
        _ => long.MaxValue,
    };
}

public sealed class AbuseOptionsValidator : IValidateOptions<AbuseOptions>
{
    public ValidateOptionsResult Validate(string? name, AbuseOptions options) =>
        options.MaxPdfBytes > 25L * 1024 * 1024 || typeof(AbuseOptions).GetProperties().Any(p => p.PropertyType == typeof(int) && (int)p.GetValue(options)! <= 0
            || p.PropertyType == typeof(long) && (long)p.GetValue(options)! <= 0)
            ? ValidateOptionsResult.Fail("All abuse limits must be positive; MaxPdfBytes cannot exceed the 25 MiB worker limit.") : ValidateOptionsResult.Success;
}

public sealed class ResourceQuotaException(string resource, bool site = false)
    : Exception(site ? "Glosify's storage capacity has been reached. Please try again later."
        : $"Your {resource.Replace('_', ' ')} limit has been reached. Delete saved content from Account usage to free space.")
{
    public string Resource { get; } = AbuseMetrics.RecordQuota(resource, site);
    public int StatusCode => site ? 503 : 409;
    public string Code => site ? "site_capacity_exceeded" : "resource_quota_exceeded";
}

internal static class AbuseMetrics
{
    private static readonly System.Diagnostics.Metrics.Meter Meter = new("Glosify.Abuse");
    private static readonly System.Diagnostics.Metrics.Counter<long> Rejections = Meter.CreateCounter<long>("glosify.quota.rejections");
    internal static string RecordQuota(string resource, bool site)
    {
        Rejections.Add(1, new KeyValuePair<string, object?>("resource", resource.StartsWith("quiz:", StringComparison.Ordinal) ? "quiz_items_per_quiz" : resource),
            new KeyValuePair<string, object?>("scope", site ? "site" : "account"));
        return resource;
    }
    internal static DateTimeOffset RecordSignup(DateTimeOffset retryAt, bool disabled)
    {
        RecordQuota(disabled ? "signup_disabled" : "signup_capacity", true);
        return retryAt;
    }
}

public sealed class SignupLimitException(DateTimeOffset retryAt, bool disabled = false)
    : Exception(new Glosify.Localization.UiTextStringLocalizer()[disabled ? "Auth.SignupsClosed" : "Auth.SignupsLimited"].Value)
{
    public DateTimeOffset RetryAt { get; } = AbuseMetrics.RecordSignup(retryAt, disabled);
}
