namespace Glosify.Models.Entities;

public sealed class RealtimeTranslationMinute
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SessionId { get; set; }
    public int MinuteIndex { get; set; }
    public Guid ReservationId { get; set; }
    public int Credits { get; set; }
    public string Status { get; set; } = RealtimeTranslationMinuteStatuses.Reserved;
    public DateTimeOffset ReservedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? BegunAt { get; set; }
    public DateTimeOffset? ReleasedAt { get; set; }
    public byte[] RowVersion { get; set; } = [];
    public RealtimeTranslationSession? Session { get; set; }
}

public static class RealtimeTranslationMinuteStatuses
{
    public const string Reserved = "reserved";
    public const string Begun = "begun";
    public const string Released = "released";
}
