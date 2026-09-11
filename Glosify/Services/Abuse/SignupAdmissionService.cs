using System.Net;
using System.Security.Cryptography;
using System.Text;
using Glosify.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Glosify.Services.Abuse;

public sealed class SignupAdmissionService(GlosifyContext db, IHttpContextAccessor http, IOptions<AbuseOptions> options, TimeProvider clock)
{
    public static string NormalizeAddress(IPAddress? address)
    {
        if (address is null) return "unknown";
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var bytes = address.GetAddressBytes();
        if (bytes.Length == 16) Array.Clear(bytes, 8, 8);
        return Convert.ToHexString(bytes);
    }

    // The caller wraps Identity creation, login association and these buckets in
    // one transaction. Sign-ins for an existing provider identity skip admission.
    public async Task AdmitAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var day = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        var hour = day.AddHours(now.Hour);
        if (!options.Value.SignupsEnabled) throw new SignupLimitException(day.AddDays(1), true);
        await ResourceAccounting.LockAsync(db, "glosify:signup-admission", ct);
        var address = NormalizeAddress(http.HttpContext?.Connection.RemoteIpAddress);
        string Hash(string window) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(window + ":" + address)));
        var limits = new[] {
            ("day:" + day.ToUnixTimeSeconds(), options.Value.SignupsPerDay, day.AddDays(1)),
            ("ip-day:" + Hash(day.ToString("O")), options.Value.SignupsPerIpDay, day.AddDays(1)),
            ("ip-hour:" + Hash(hour.ToString("O")), options.Value.SignupsPerIpHour, hour.AddHours(1)) };
        foreach (var (id, limit, expires) in limits)
        {
            var bucket = await db.Set<SignupBucket>().FindAsync([id], ct);
            if (bucket?.Count >= limit) throw new SignupLimitException(expires);
            if (bucket is null) { bucket = new() { Id = id, ExpiresAt = expires }; db.Add(bucket); }
            bucket.Count++;
        }
    }
}
