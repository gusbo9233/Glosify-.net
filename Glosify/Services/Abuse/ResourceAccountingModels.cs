namespace Glosify.Services.Abuse;

public sealed class ResourceUsage
{
    public string Scope { get; set; } = "";
    public string Resource { get; set; } = "";
    public long Used { get; set; }
}

// Snapshots make accounting independent of loaded navigation properties and allow
// cascade deletions to release the exact original charge, even after parents disappear.
public sealed class ResourceEntry
{
    public string Id { get; set; } = "";
    public string UserId { get; set; } = "";
    public string EntityType { get; set; } = "";
    public string EntityKeyJson { get; set; } = "[]";
    public string CascadeAncestors { get; set; } = "";
    public string ChargesJson { get; set; } = "{}";
}

public sealed class ResourceReservation
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = "";
    public string ChargesJson { get; set; } = "{}";
    public DateTimeOffset ExpiresAt { get; set; }
    public string? BlobName { get; set; }
}

public sealed class BlobCleanupRequest
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = "";
    public string BlobName { get; set; } = "";
    public long Bytes { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class SignupBucket
{
    public string Id { get; set; } = "";
    public int Count { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

public sealed class ResourceAccountingState
{
    public int Id { get; set; }
    public bool Ready { get; set; }
}
