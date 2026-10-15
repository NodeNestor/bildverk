using Bildverk.Caching;
using Bildverk.Commands;
using Bildverk.Internal;
using Bildverk.Processing;
using Bildverk.Sources;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bildverk;

/// <summary>
/// Cache-or-process logic shared by the middleware (requests) and the <see cref="BackgroundOptimizer"/>.
/// Owns the per-key locks, master copies and the processing gate.
/// </summary>
internal sealed class ImagePipeline(
    IOptionsMonitor<BildverkOptions> options,
    IImageCache cache,
    IImageProcessor processor,
    ProcessingGate gate,
    TimeProvider time,
    IHostApplicationLifetime lifetime,
    ILogger<ImagePipeline> logger)
{
    private readonly AsyncKeyedLock _locks = new();

    /// <summary>Returns the cached result, or processes it (once, even under concurrent requests) and caches it.</summary>
    public async Task<CachedImage> GetOrCreateAsync(string key, string canonical, string path, SourceImage original, ImageCommands effective, CancellationToken ct)
    {
        var opts = options.CurrentValue;
        var cached = await cache.GetAsync(key, ct);
        if (IsValid(cached, canonical, original, opts)) return cached!;

        using (await _locks.AcquireAsync(key, ct))
        {
            // Someone else may have produced it while we waited.
            cached = await cache.GetAsync(key, ct);
            if (IsValid(cached, canonical, original, opts)) return cached!;

            var input = await SelectInputAsync(path, original, effective, opts, ct);
            var result = await RunAsync(key, original, input, effective, opts.Defaults.Encoder, opts.Limits.ProcessingTimeout, opts, ct);
            var meta = new CacheEntryMetadata(result.ContentType, original.LastModifiedUtc, original.Length, time.GetUtcNow(), result.Width, result.Height, Canonical: canonical);
            return await cache.SetAsync(key, result.Data, meta, lifetime.ApplicationStopping);
        }
    }

    /// <summary>
    /// Re-encodes an existing entry with <paramref name="target"/> settings and replaces it when the result is
    /// meaningfully smaller. Either way the entry is marked optimized so it is not attempted again.
    /// </summary>
    public async Task<OptimizeOutcome> OptimizeAsync(string key, string canonical, string path, SourceImage original, ImageCommands effective, EncoderSettings target, CancellationToken ct)
    {
        var opts = options.CurrentValue;
        var before = await cache.GetAsync(key, ct);
        if (!IsValid(before, canonical, original, opts) || before!.Metadata.Optimized) return OptimizeOutcome.Skipped;

        var input = await SelectInputAsync(path, original, effective, opts, ct);
        ProcessedImage? result;
        try
        {
            result = await RunAsync(key, original, input, effective, target, opts.Optimize.Timeout, opts, ct);
        }
        catch (ImageProcessingException ex)
        {
            // Too slow or failed at high effort: keep the fast encode and never try this entry again.
            Log.OptimizeFailed(logger, key, ex.Message);
            result = null;
        }

        using (await _locks.AcquireAsync(key, ct))
        {
            // The entry may have been replaced while we were encoding (source changed, cache rebuilt ...).
            var current = await cache.GetAsync(key, ct);
            if (current is null || current.Metadata.CachedAtUtc != before.Metadata.CachedAtUtc || current.Metadata.Optimized)
                return OptimizeOutcome.Skipped;

            var smaller = result is not null && result.Data.Length <= current.Length * (1 - opts.Optimize.MinSavingsPercent / 100.0);
            byte[] data;
            if (smaller)
            {
                data = result!.Data;
            }
            else
            {
                await using var s = await current.OpenReadAsync(ct);
                using var ms = new MemoryStream((int)current.Length);
                await s.CopyToAsync(ms, ct);
                data = ms.ToArray();
            }

            var meta = current.Metadata with { Optimized = true, Width = smaller ? result!.Width : current.Metadata.Width, Height = smaller ? result!.Height : current.Metadata.Height };
            await cache.SetAsync(key, data, meta, lifetime.ApplicationStopping);
            Log.Optimized(logger, key, current.Length, result?.Data.Length, smaller ? "" : " (kept original encode)");
            return smaller ? OptimizeOutcome.Replaced : OptimizeOutcome.KeptExisting;
        }
    }

    public bool IsValid(CachedImage? cached, string canonical, SourceImage original, BildverkOptions opts) =>
        cached is not null
        && string.Equals(cached.Metadata.Canonical, canonical, StringComparison.Ordinal)
        && cached.Metadata.SourceLastModifiedUtc == original.LastModifiedUtc
        && cached.Metadata.SourceLength == original.Length
        && time.GetUtcNow() - cached.Metadata.CachedAtUtc <= opts.Cache.MaxAge;

    /// <summary>The original, or its master copy when that is enabled, worthwhile and big enough for this request.</summary>
    private async Task<SourceImage> SelectInputAsync(string path, SourceImage original, ImageCommands c, BildverkOptions opts, CancellationToken ct)
    {
        if (!opts.Masters.Enabled || !c.HasResize || original.Length < opts.Masters.MinSourceBytes
            || !await IsWorthAMasterAsync(original, opts.Masters, ct))
            return original;

        var master = await GetMasterAsync(path, original, opts, ct);
        return CanUseMaster(master.Metadata, c, opts.Masters)
            ? new SourceImage(master.LastModifiedUtc, master.Length, master.OpenReadAsync)
            : original;
    }

    private async Task<ProcessedImage> RunAsync(string key, SourceImage original, SourceImage input, ImageCommands c, EncoderSettings encoder, TimeSpan timeout, BildverkOptions opts, CancellationToken requestAborted)
    {
        await gate.WaitAsync(requestAborted);
        try
        {
            // Not tied to the request: other requests may be waiting for this result.
            var ct = lifetime.ApplicationStopping;
            await using var stream = await input.OpenReadAsync(ct);
            var started = time.GetTimestamp();
            var result = await processor.ProcessAsync(stream, new ProcessingRequest(c, c.Format!.Value, c.Quality ?? 0, opts.Limits, encoder, timeout), ct);
            Log.Processed(logger, key, result.Data.Length, ReferenceEquals(input, original) ? "" : ", from master", time.GetElapsedTime(started).TotalMilliseconds);
            return result;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<CachedImage> GetMasterAsync(string path, SourceImage original, BildverkOptions opts, CancellationToken ct)
    {
        var m = opts.Masters;
        var commands = new ImageCommands { Width = m.Size, Height = m.Size, Format = OutputFormat.Jpeg, Quality = m.Quality, NoUpscale = true };
        var (key, canonical) = BildverkMiddleware.CacheKeyFor("master:" + path, commands, opts.Cache.HashLength, new EncoderSettings());

        var cached = await cache.GetAsync(key, ct);
        if (IsValid(cached, canonical, original, opts)) return cached!;

        using (await _locks.AcquireAsync(key, ct))
        {
            cached = await cache.GetAsync(key, ct);
            if (IsValid(cached, canonical, original, opts)) return cached!;

            var result = await RunAsync(key, original, original, commands, new EncoderSettings(), opts.Limits.ProcessingTimeout, opts, ct);
            var meta = new CacheEntryMetadata(result.ContentType, original.LastModifiedUtc, original.Length, time.GetUtcNow(), result.Width, result.Height, Canonical: canonical);
            return await cache.SetAsync(key, result.Data, meta, lifetime.ApplicationStopping);
        }
    }

    /// <summary>A master only pays off when the original is clearly larger than it (≥ 1.5× on the long side).</summary>
    private async Task<bool> IsWorthAMasterAsync(SourceImage original, MasterOptions options, CancellationToken ct)
    {
        await using var stream = await original.OpenReadAsync(ct);
        var size = await processor.IdentifyAsync(stream, ct);
        return size is { } s && Math.Max(s.Width, s.Height) >= options.Size * 1.5;
    }

    /// <summary>
    /// Only when the master really is a downscale (else it's just a re-encoded original) and the request
    /// would not have to upscale it.
    /// </summary>
    private static bool CanUseMaster(CacheEntryMetadata master, ImageCommands c, MasterOptions options)
    {
        if (master.Width <= 0 || master.Height <= 0 || Math.Max(master.Width, master.Height) < options.Size) return false;
        var plan = ResizePlan.Create(master.Width, master.Height, c);
        return plan.ScaledWidth <= master.Width && plan.ScaledHeight <= master.Height;
    }
}

internal enum OptimizeOutcome { Skipped, Replaced, KeptExisting }
