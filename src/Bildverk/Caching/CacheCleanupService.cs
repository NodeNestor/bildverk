using Bildverk.Internal;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bildverk.Caching;

/// <summary>Periodically removes expired cache entries and enforces <see cref="CacheOptions.MaxSizeMB"/>.</summary>
internal sealed class CacheCleanupService(
    IImageCache cache,
    IOptionsMonitor<BildverkOptions> options,
    TimeProvider time,
    ILogger<CacheCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var interval = options.CurrentValue.Cache.CleanupInterval;
            if (interval <= TimeSpan.Zero)
            {
                // Disabled; check again later in case the setting changes.
                await Delay(TimeSpan.FromMinutes(5), stoppingToken);
                continue;
            }

            await Delay(interval, stoppingToken);
            if (stoppingToken.IsCancellationRequested) break;

            try
            {
                var r = await cache.CleanupAsync(stoppingToken);
                if (r.Expired + r.Evicted + r.Orphans > 0)
                    Log.CleanedUp(logger, r.Expired, r.Evicted, r.Orphans, r.BytesRemaining / 1048576.0);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.CleanupFailed(logger, ex);
            }
        }
    }

    private Task Delay(TimeSpan delay, CancellationToken ct) =>
        Task.Delay(delay, time, ct).ContinueWith(_ => { }, TaskScheduler.Default);
}
