namespace Glosify.Services.Abuse;

// A provider result can be saved after its client method returns. Keep its capacity
// reserved through the HTTP request, then release anything that was not persisted.
public sealed class RequestResourceReservations
{
    internal readonly List<(Guid Id, string UserId)> Pending = [];
    internal readonly HashSet<Guid> Consumed = [];
    public async Task ReleaseAsync(ResourceQuotaService quotas)
    {
        // Release is idempotent. Include consumed claims because the enclosing
        // feature transaction might have rolled back after SaveChanges returned.
        foreach (var (id, user) in Pending)
            await quotas.ReleaseAsync(id, user, CancellationToken.None);
        Pending.Clear();
        Consumed.Clear();
    }
}
