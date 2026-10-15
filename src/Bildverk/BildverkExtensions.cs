using Bildverk.Caching;
using Bildverk.Processing;
using Bildverk.Security;
using Bildverk.Sources;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Bildverk;

public static class BildverkExtensions
{
    /// <summary>
    /// Registers Bildverk with options bound from the "Bildverk" section of configuration (appsettings.json,
    /// environment variables, ...). <paramref name="configure"/> runs after binding.
    /// Register your own <see cref="IImageSource"/>, <see cref="IImageCache"/> or <see cref="IImageProcessor"/>
    /// before or after this call to replace the defaults.
    /// </summary>
    public static IServiceCollection AddBildverk(this IServiceCollection services, Action<BildverkOptions>? configure = null)
    {
        var builder = services.AddOptions<BildverkOptions>().BindConfiguration(BildverkOptions.SectionName);
        if (configure is not null) builder.Configure(configure);
        builder.Validate(Validate, "Invalid Bildverk configuration: check sizes, HashLength (16–64), qualities (1–100), efforts (WebP 0–6, AVIF 0–9) and format names.").ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<BildverkOptions>, BildverkPathValidator>());

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IImageSource, FileSystemImageSource>();
        services.TryAddSingleton<IImageCache, FileSystemImageCache>();
        services.TryAddSingleton<IImageProcessor, VipsImageProcessor>();
        services.TryAddSingleton<IBildverkUrlSigner, BildverkUrlSigner>();
        services.TryAddSingleton<ProcessingGate>();
        services.TryAddSingleton<ImagePipeline>();
        services.TryAddSingleton<BackgroundOptimizer>();
        services.AddHostedService(sp => sp.GetRequiredService<BackgroundOptimizer>());
        services.AddHostedService<CacheCleanupService>();
        services.AddHostedService<BildverkStartupCheck>();
        return services;
    }

    /// <summary>
    /// Adds the image middleware. Call it before UseStaticFiles / MapStaticAssets
    /// so processed requests are answered before static files serve the original.
    /// </summary>
    public static IApplicationBuilder UseBildverk(this IApplicationBuilder app) => app.UseMiddleware<BildverkMiddleware>();

    private static bool Validate(BildverkOptions o) =>
        o.Cache.HashLength is >= 16 and <= 64
        && o.Cache.FolderDepth >= 0
        && (string.IsNullOrEmpty(o.Security.HmacKey) || o.Security.HmacKey.Length >= 16)
        && !string.IsNullOrWhiteSpace(o.Cache.Path)
        && o.Cache.MaxSizeMB >= 0
        && o.Limits.MaxWidth > 0 && o.Limits.MaxHeight > 0
        && o.Limits.MaxSourcePixels > 0 && o.Limits.MaxSourceBytes > 0
        && o.Limits.MaxConcurrentProcessing is null or > 0 && o.Limits.MaxQueuedRequests > 0 && o.Limits.ThreadsPerImage > 0
        && o.Limits.ProcessingTimeout > TimeSpan.Zero && o.Optimize.Timeout > TimeSpan.Zero
        && o.Defaults.Quality is >= 1 and <= 100
        && o.Defaults.WebpEffort is >= 0 and <= 6 && o.Defaults.AvifEffort is >= 0 and <= 9
        && o.Masters.Size > 0 && o.Masters.Quality is >= 1 and <= 100
        && o.Optimize.WebpEffort is >= 0 and <= 6 && o.Optimize.AvifEffort is >= 0 and <= 9
        && o.Optimize.MaxConcurrent > 0 && o.Optimize.QueueLimit > 0 && o.Optimize.MinSavingsPercent is >= 0 and < 100
        && (o.Formats.Preference ?? []).All(p => Commands.CommandParser.TryFormat(p, out _))
        && (o.Limits.AllowedWidths ?? []).All(w => w > 0) && (o.Limits.AllowedHeights ?? []).All(h => h > 0)
        && (o.Limits.AllowedInputFormats ?? []).All(f => VipsImageProcessor.InputLoaders.ContainsKey(f));
}
