using Bildverk.Internal;
using System.Security.Cryptography;
using System.Text;
using Bildverk.Caching;
using Bildverk.Commands;
using Bildverk.Processing;
using Bildverk.Security;
using Bildverk.Sources;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Headers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace Bildverk;

/// <summary>
/// request → parse commands → find original → pick output format → cache hit and still valid? serve it
/// : lock key → process → write cache → serve.
/// Requests without image commands go to the next middleware (normally static files) untouched,
/// unless <see cref="FormatOptions.AutoForPlainRequests"/> converts them to a better format.
/// </summary>
internal sealed class BildverkMiddleware(
    RequestDelegate next,
    IOptionsMonitor<BildverkOptions> options,
    IImageSource source,
    ImagePipeline pipeline,
    BackgroundOptimizer optimizer,
    ILogger<BildverkMiddleware> logger)
{
    /// <summary>Part of every cache key. Bump when processing output changes so old entries are not reused.</summary>
    internal const string ProcessorVersion = "2";

    public async Task InvokeAsync(HttpContext context)
    {
        var request = context.Request;
        var path = request.Path.Value;
        var ext = Path.GetExtension(path);
        var opts = options.CurrentValue;
        if (!(HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method))
            || string.IsNullOrEmpty(path) || string.IsNullOrEmpty(ext)
            || !(opts.Extensions ?? BildverkOptions.DefaultExtensions).Contains(ext, StringComparer.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        // Null bytes and other control characters are never part of a real file name, and some
        // file APIs throw on them: answer 400 instead of letting them reach the file system.
        if (path.AsSpan().IndexOfAnyInRange('\0', '\u001f') >= 0 || path.Contains('\u007f'))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var commands = request.QueryString.HasValue ? CommandParser.Parse(request.Query) : null;
        var plain = commands is null;
        if (plain)
        {
            // A plain image URL: only ours to handle when originals should also get the best format.
            if (!opts.Formats.AutoForPlainRequests || IsGif(ext))
            {
                await next(context);
                return;
            }
            commands = new ImageCommands { AutoFormat = true };
        }
        else if (!string.IsNullOrEmpty(opts.Security.HmacKey)
            && !BildverkUrlSigner.Verify(opts.Security.HmacKey, path, commands!, request.Query[CommandParser.HmacParameter].LastOrDefault()))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var (effective, negotiated) = Resolve(commands!, ext, opts, request.Headers.Accept);

        // The response now depends on the Accept header: tell browsers and CDNs, also when we fall back.
        if (negotiated) context.Response.Headers.Append(HeaderNames.Vary, HeaderNames.Accept);

        // Plain request and the browser gets the original format anyway: nothing to do.
        if (plain && CommandParser.TryFormat(ext, out var sourceFormat) && effective.Format == sourceFormat)
        {
            await next(context);
            return;
        }

        var original = await source.GetAsync(path, context.RequestAborted);
        if (original is null)
        {
            await next(context);
            return;
        }

        var (key, canonical) = CacheKeyFor(path, effective, opts.Cache.HashLength, opts.Defaults.Encoder);

        try
        {
            var cached = await pipeline.GetOrCreateAsync(key, canonical, path, original, effective, context.RequestAborted);

            if (!cached.Metadata.Optimized && BackgroundOptimizer.TargetFor(opts, effective.Format!.Value) is not null)
                optimizer.TryEnqueue(new OptimizeJob(key, canonical, path, effective));

            await ServeAsync(context, cached, key, original, opts);
        }
        catch (ImageProcessingException ex)
        {
            // Escaped forms only: decoded paths could carry newlines and forge log lines.
            Log.Rejected(logger, request.Path.ToUriComponent(), request.QueryString.ToUriComponent(), ex.Message);
            if (!context.Response.HasStarted) context.Response.StatusCode = ex.StatusCode;
        }
    }

    private static async Task ServeAsync(HttpContext context, CachedImage image, string key, SourceImage original, BildverkOptions opts)
    {
        var response = context.Response;
        // Same source + same commands = same bytes, so the ETag survives cache rebuilds and multiple servers.
        // "-o" marks the background-optimized bytes, so browsers holding the first encode fetch the new one.
        var etag = new EntityTagHeaderValue($"\"{key}-{original.LastModifiedUtc.UtcTicks:x}-{original.Length:x}{(image.Metadata.Optimized ? "-o" : "")}\"");
        var lastModified = original.LastModifiedUtc > image.LastModifiedUtc ? original.LastModifiedUtc : image.LastModifiedUtc;

        var headers = response.GetTypedHeaders();
        headers.ETag = etag;
        headers.LastModified = lastModified;
        headers.CacheControl = new CacheControlHeaderValue { Public = true, MaxAge = opts.Browser.MaxAge };
        response.Headers.XContentTypeOptions = "nosniff";

        if (IsNotModified(context.Request.GetTypedHeaders(), etag, lastModified))
        {
            response.StatusCode = StatusCodes.Status304NotModified;
            return;
        }

        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = image.Metadata.ContentType;
        response.ContentLength = image.Length;
        if (HttpMethods.IsHead(context.Request.Method)) return;

        if (image.PhysicalPath is not null)
        {
            await response.SendFileAsync(image.PhysicalPath, 0, image.Length, context.RequestAborted);
            return;
        }

        await using var stream = await image.OpenReadAsync(context.RequestAborted);
        await stream.CopyToAsync(response.Body, context.RequestAborted);
    }

    private static bool IsNotModified(RequestHeaders request, EntityTagHeaderValue etag, DateTimeOffset lastModified)
    {
        if (request.IfNoneMatch is { Count: > 0 } ifNoneMatch)
            return ifNoneMatch.Any(t => t.Equals(EntityTagHeaderValue.Any) || t.Compare(etag, useStrongComparison: false));

        // HTTP dates have one-second resolution.
        return request.IfModifiedSince is { } since && lastModified.UtcTicks / TimeSpan.TicksPerSecond <= since.UtcTicks / TimeSpan.TicksPerSecond;
    }

    private static bool IsGif(string ext) => ext.Equals(".gif", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Clamps to the limits and fills in format and quality, so the cache key reflects what is actually produced.
    /// Negotiated = the format was chosen from the Accept header (format=auto or <see cref="FormatOptions.AutoByDefault"/>).
    /// </summary>
    internal static (ImageCommands Effective, bool Negotiated) Resolve(ImageCommands c, string extension, BildverkOptions opts, string? accept = null)
    {
        var sourceFormat = CommandParser.TryFormat(extension, out var f) ? f : OutputFormat.Jpeg;
        var negotiated = false;
        OutputFormat format;
        if (c.Format is { } explicitFormat)
        {
            format = explicitFormat;
        }
        else if ((c.AutoFormat || opts.Formats.AutoByDefault) && sourceFormat != OutputFormat.Gif)
        {
            // GIFs are left alone: converting would silently drop animation.
            negotiated = true;
            format = Negotiate(accept, opts.Formats.Preference ?? FormatOptions.DefaultPreference) ?? sourceFormat;
        }
        else
        {
            format = sourceFormat;
        }

        var lossy = format is OutputFormat.Jpeg or OutputFormat.WebP or OutputFormat.Avif;
        var effective = c with
        {
            Width = c.Width is { } w ? Snap(Math.Min(w, Math.Max(1, opts.Limits.MaxWidth)), opts.Limits.AllowedWidths) : null,
            Height = c.Height is { } h ? Snap(Math.Min(h, Math.Max(1, opts.Limits.MaxHeight)), opts.Limits.AllowedHeights) : null,
            Format = format,
            AutoFormat = false,
            Quality = lossy ? Math.Clamp(c.Quality ?? opts.Defaults.Quality, 1, 100) : null,
        };
        return (effective, negotiated);
    }

    /// <summary>Smallest allowed size that is at least <paramref name="value"/>, else the largest allowed; unchanged when no list is set.</summary>
    internal static int Snap(int value, List<int>? allowed)
    {
        if (allowed is not { Count: > 0 }) return value;
        int? up = null, max = null;
        foreach (var a in allowed)
        {
            if (a <= 0) continue;
            if (a >= value && (up is null || a < up)) up = a;
            if (max is null || a > max) max = a;
        }
        return up ?? max ?? value;
    }

    /// <summary>First format in <paramref name="preference"/> the browser explicitly accepts (wildcards don't count).</summary>
    internal static OutputFormat? Negotiate(string? accept, IEnumerable<string> preference)
    {
        if (string.IsNullOrWhiteSpace(accept) || !MediaTypeHeaderValue.TryParseList(accept.Split(','), out var types)) return null;

        var accepted = types
            .Where(t => t.Quality is not 0)
            .Select(t => t.MediaType.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var name in preference)
            if (CommandParser.TryFormat(name, out var format) && accepted.Contains(ContentTypes.For(format)))
                return format;
        return null;
    }

    internal static string CacheKey(string path, ImageCommands effective, int hashLength, EncoderSettings? encoder = null) =>
        CacheKeyFor(path, effective, hashLength, encoder).Key;

    /// <summary>The canonical description of what is produced, and its hash (the cache file name).</summary>
    internal static (string Key, string Canonical) CacheKeyFor(string path, ImageCommands effective, int hashLength, EncoderSettings? encoder = null)
    {
        // Encoder settings change the output bytes, so they are part of the key.
        var e = encoder?.KeyPart(effective.Format ?? OutputFormat.Jpeg) ?? "";
        var canonical = $"v{ProcessorVersion}|{path}?{CommandParser.Canonicalize(effective)}{(e.Length > 0 ? "|" + e : "")}";
        var hex = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        return (hex[..Math.Clamp(hashLength, 16, 64)], canonical);
    }
}

/// <summary>Caps how many images are processed at once (<see cref="LimitsOptions.MaxConcurrentProcessing"/>).</summary>
internal sealed class ProcessingGate(IOptions<BildverkOptions> options) : IDisposable
{
    private readonly SemaphoreSlim _semaphore = new(Math.Max(1, options.Value.Limits.EffectiveMaxConcurrentProcessing));

    public Task WaitAsync(CancellationToken cancellationToken) => _semaphore.WaitAsync(cancellationToken);

    public void Release() => _semaphore.Release();

    public void Dispose() => _semaphore.Dispose();
}
