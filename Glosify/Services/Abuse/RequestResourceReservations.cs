namespace Glosify.Services.Abuse;

// A provider result can be saved after its client method returns. Keep its capacity
// reserved through the HTTP request, then release anything that was not persisted.
public sealed class RequestResourceReservations
{
    private readonly List<(Guid Id, string UserId)> _pending = [];
    internal (Guid Id, string UserId)[] Pending { get { lock (_pending) return _pending.ToArray(); } }
    internal readonly HashSet<Guid> Consumed = [];
    internal void Track(Guid id, string userId) { lock (_pending) _pending.Add((id, userId)); }

    internal async Task RenewWhileActiveAsync(ResourceQuotaService quotas, CancellationTokenSource request,
        TimeSpan? interval = null)
    {
        using var timer = new PeriodicTimer(interval ?? TimeSpan.FromMinutes(1));
        try
        {
            while (await timer.WaitForNextTickAsync(request.Token))
                foreach (var (id, user) in Pending)
                    await quotas.RenewAsync(id, user, request.Token);
        }
        // SQL Server can surface cancellation during savepoint creation as a
        // SqlException rather than OperationCanceledException.
        catch (Exception ex) when (request.IsCancellationRequested && ex is
            OperationCanceledException or Microsoft.Data.SqlClient.SqlException or Microsoft.EntityFrameworkCore.DbUpdateException) { }
        catch { request.Cancel(); throw; }
    }
    public async Task ReleaseAsync(ResourceQuotaService quotas)
    {
        // Release is idempotent. Include consumed claims because the enclosing
        // feature transaction might have rolled back after SaveChanges returned.
        foreach (var (id, user) in Pending)
            await quotas.ReleaseAsync(id, user, CancellationToken.None);
        lock (_pending) _pending.Clear();
        Consumed.Clear();
    }
}
