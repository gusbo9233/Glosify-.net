namespace Glosify.Models.Entities;

public sealed class AiMonthlyBudget
{
    public string PeriodKey { get; set; } = string.Empty;
    public long LimitMicros { get; set; }
    public long SpentMicros { get; set; }
    public long ReservedMicros { get; set; }
    public long OverrunMicros { get; set; }
    public DateTimeOffset? ExhaustedAt { get; set; }
    public string? ExhaustedReason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public byte[] RowVersion { get; set; } = [];

    public long AvailableMicros => Math.Max(0, LimitMicros - SpentMicros - ReservedMicros);
}
