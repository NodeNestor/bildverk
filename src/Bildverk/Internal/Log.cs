using Microsoft.Extensions.Logging;

namespace Bildverk.Internal;

/// <summary>All log messages, source-generated: no allocations or formatting when the level is disabled.</summary>
internal static partial class Log
{
    [LoggerMessage(1, LogLevel.Debug, "Bildverk processed {Key} ({Bytes} bytes{FromMaster}) in {Ms:0} ms")]
    public static partial void Processed(ILogger logger, string key, int bytes, string fromMaster, double ms);

    [LoggerMessage(2, LogLevel.Warning, "Bildverk rejected {Path}{Query}: {Reason}")]
    public static partial void Rejected(ILogger logger, string path, string query, string reason);

    [LoggerMessage(3, LogLevel.Debug, "Bildverk optimized {Key}: {Before} → {After} bytes{Kept}")]
    public static partial void Optimized(ILogger logger, string key, long before, int? after, string kept);

    [LoggerMessage(4, LogLevel.Debug, "Bildverk could not optimize {Key}: {Reason}")]
    public static partial void OptimizeFailed(ILogger logger, string key, string reason);

    [LoggerMessage(5, LogLevel.Warning, "Bildverk background optimization failed for {Path}")]
    public static partial void OptimizeCrashed(ILogger logger, Exception exception, string path);

    [LoggerMessage(6, LogLevel.Information, "Bildverk cache cleanup: {Expired} expired, {Evicted} evicted, {Orphans} orphans removed; {MB:0.0} MB in use")]
    public static partial void CleanedUp(ILogger logger, int expired, int evicted, int orphans, double mb);

    [LoggerMessage(7, LogLevel.Error, "Bildverk cache cleanup failed")]
    public static partial void CleanupFailed(ILogger logger, Exception exception);

    [LoggerMessage(8, LogLevel.Debug, "Bildverk could not read cache metadata {Path}")]
    public static partial void UnreadableMetadata(ILogger logger, Exception exception, string path);

    [LoggerMessage(9, LogLevel.Warning, "Bildverk: {Warning}")]
    public static partial void ConfigWarning(ILogger logger, string warning);
}
