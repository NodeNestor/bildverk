using Bildverk.Internal;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bildverk;

/// <summary>Logs a warning at startup for configurations that are valid but easy to abuse.</summary>
internal sealed class BildverkStartupCheck(IOptions<BildverkOptions> options, ILogger<BildverkStartupCheck> logger) : IHostedService
{
    internal const int RecommendedHmacKeyLength = 32;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var warning in Warnings(options.Value)) Log.ConfigWarning(logger, warning);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    internal static IEnumerable<string> Warnings(BildverkOptions o)
    {
        var signed = !string.IsNullOrEmpty(o.Security.HmacKey);
        var fixedSizes = o.Limits.AllowedWidths is { Count: > 0 } || o.Limits.AllowedHeights is { Count: > 0 };

        if (!signed && !fixedSizes && o.Cache.MaxSizeMB == 0)
            yield return "URLs are unsigned, sizes are unrestricted and the cache has no size limit: anyone can fill the disk with size variants. " +
                         "Set Security:HmacKey, Limits:AllowedWidths/AllowedHeights or Cache:MaxSizeMB.";
        else if (!signed && !fixedSizes)
            yield return $"URLs are unsigned and sizes are unrestricted: anyone can create many size variants (disk use is capped at Cache:MaxSizeMB = {o.Cache.MaxSizeMB} MB). " +
                         "For public sites consider Security:HmacKey or Limits:AllowedWidths/AllowedHeights.";

        if (signed && o.Security.HmacKey!.Length < RecommendedHmacKeyLength)
            yield return $"Security:HmacKey is {o.Security.HmacKey.Length} characters; use at least {RecommendedHmacKeyLength} random characters.";
    }
}
