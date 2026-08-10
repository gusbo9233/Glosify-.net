using Glosify.Services.Ai;

namespace Glosify.Tests;

internal sealed class AlwaysAvailablePaidServiceGate : IPaidServiceGate
{
    public Task<PaidServiceStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new PaidServiceStatus(true, null, DateTimeOffset.MaxValue));

    public Task EnsureAvailableAsync(CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
