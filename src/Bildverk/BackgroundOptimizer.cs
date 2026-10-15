using Bildverk.Internal;
using System.Collections.Concurrent;
using System.Threading.Channels;
using Bildverk.Commands;
using Bildverk.Processing;
using Bildverk.Sources;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bildverk;

/// <summary>One cached image to re-encode at high effort.</summary>
internal sealed record OptimizeJob(string Key, string Canonical, string Path, ImageCommands Effective);

/// <summary>
/// "Fast first, small later": requests are answered with the fast encoder settings, then this service
/// re-encodes WebP/AVIF entries at <see cref="OptimizeOptions"/> effort in the background and swaps the
/// cached file when it is smaller. Visitors never wait for the slow encoder.
/// </summary>
internal sealed class BackgroundOptimizer : BackgroundService
{
    private readonly Channel<OptimizeJob> _queue;
    private readonly ConcurrentDictionary<string, byte> _pending = new(StringComparer.Ordinal);
    private readonly IOptionsMonitor<BildverkOptions> _options;
    private readonly IImageSource _source;
    private readonly ImagePipeline _pipeline;
    private readonly ILogger<BackgroundOptimizer> _logger;
    private readonly int _workers;

    public BackgroundOptimizer(IOptionsMonitor<BildverkOptions> options, IImageSource source, ImagePipeline pipeline, ILogger<BackgroundOptimizer> logger)
    {
        _options = options;
        _source = source;
        _pipeline = pipeline;
        _logger = logger;
        var o = options.CurrentValue.Optimize;
        _workers = Math.Max(1, o.MaxConcurrent);
        _queue = Channel.CreateBounded<OptimizeJob>(new BoundedChannelOptions(Math.Max(1, o.QueueLimit))
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleWriter = false,
        });
    }

    /// <summary>The encoder settings an entry of this format should be optimized to, or null when there is nothing to gain.</summary>
    public static EncoderSettings? TargetFor(BildverkOptions opts, OutputFormat format)
    {
        var o = opts.Optimize;
        if (!o.Enabled) return null;
        var d = opts.Defaults;
        return format switch
        {
            OutputFormat.WebP when o.WebpEffort > d.WebpEffort => d.Encoder with { WebpEffort = o.WebpEffort },
            OutputFormat.Avif when o.AvifEffort > d.AvifEffort => d.Encoder with { AvifEffort = o.AvifEffort },
            _ => null,
        };
    }

    /// <summary>Queues a job unless it is already queued or the queue is full (it will be offered again on a later hit).</summary>
    public bool TryEnqueue(OptimizeJob job)
    {
        if (!_pending.TryAdd(job.Key, 0)) return false;
        if (_queue.Writer.TryWrite(job)) return true;
        _pending.TryRemove(job.Key, out _);
        return false;
    }

    /// <summary>Jobs queued or running. Useful for tests and health checks.</summary>
    public int Pending => _pending.Count;

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(Enumerable.Range(0, _workers).Select(_ => WorkAsync(stoppingToken)));

    private async Task WorkAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var job in _queue.Reader.ReadAllAsync(ct))
            {
                try
                {
                    var opts = _options.CurrentValue;
                    var target = TargetFor(opts, job.Effective.Format!.Value);
                    var original = target is null ? null : await _source.GetAsync(job.Path, ct);
                    if (target is not null && original is not null)
                        await _pipeline.OptimizeAsync(job.Key, job.Canonical, job.Path, original, job.Effective, target, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Log.OptimizeCrashed(_logger, ex, Uri.EscapeDataString(job.Path));
                }
                finally
                {
                    _pending.TryRemove(job.Key, out _);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }
}
