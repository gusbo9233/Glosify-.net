namespace Glosify.Infrastructure.Concurrency;

public interface IKeyedAsyncLock
{
    ValueTask<IAsyncDisposable> AcquireAsync(string key, CancellationToken cancellationToken = default);
}
