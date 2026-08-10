namespace Glosify.Infrastructure.Api;

public static class ApiErrorCodes
{
    public const string BadRequest = "bad_request";
    public const string ValidationFailed = "validation_failed";
    public const string Unauthorized = "unauthorized";
    public const string Forbidden = "forbidden";
    public const string NotFound = "not_found";
    public const string Conflict = "conflict";
    public const string PaymentRequired = "payment_required";
    public const string Gone = "gone";
    public const string UnprocessableEntity = "unprocessable_entity";
    public const string RateLimited = "rate_limited";
    public const string DependencyUnavailable = "dependency_unavailable";
    public const string PaidServicesBudgetExhausted = "paid_services_budget_exhausted";
    public const string UpstreamFailure = "upstream_failure";
    public const string Timeout = "timeout";
    public const string Unexpected = "unexpected_error";
    public const string CollectionParentNotFound = "collection_parent_not_found";
    public const string CollectionNameConflict = "collection_name_conflict";
    public const string QuizCollectionNotFound = "quiz_collection_not_found";
}
