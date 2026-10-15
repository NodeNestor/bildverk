namespace Bildverk;

/// <summary>
/// Root options, bound from the "Bildverk" section of appsettings.json. Settings are re-read on every
/// request, so edits apply without a restart, except the few documented as "Requires a restart"
/// (source root, processing concurrency, threads per image, optimizer workers and queue size).
/// </summary>
public sealed class BildverkOptions
{
    public const string SectionName = "Bildverk";

    public static readonly IReadOnlyList<string> DefaultExtensions = [".jpg", ".jpeg", ".png", ".webp", ".gif", ".avif", ".tif", ".tiff"];

    /// <summary>
    /// File extensions the middleware will process. Anything else passes through untouched.
    /// Null means <see cref="DefaultExtensions"/>. (Nullable on purpose: the configuration binder
    /// appends to pre-filled lists instead of replacing them.)
    /// </summary>
    public List<string>? Extensions { get; set; }

    public SourceOptions Source { get; set; } = new();
    public CacheOptions Cache { get; set; } = new();
    public BrowserOptions Browser { get; set; } = new();
    public LimitsOptions Limits { get; set; } = new();
    public DefaultsOptions Defaults { get; set; } = new();
    public SecurityOptions Security { get; set; } = new();
    public MasterOptions Masters { get; set; } = new();
    public FormatOptions Formats { get; set; } = new();
    public OptimizeOptions Optimize { get; set; } = new();
}

/// <summary>
/// Automatic output format from the browser's Accept header. Per request with <c>format=auto</c>,
/// or for every request with the switches below. Responses then carry <c>Vary: Accept</c>.
/// </summary>
public sealed class FormatOptions
{
    public static readonly IReadOnlyList<string> DefaultPreference = ["avif", "webp"];

    /// <summary>Requests with commands but no <c>format=</c> get the best format the browser accepts.</summary>
    public bool AutoByDefault { get; set; }

    /// <summary>
    /// Also plain image URLs without any query (/img/photo.jpg) are converted when the browser accepts a better format.
    /// GIFs are never converted (animation would be lost).
    /// </summary>
    public bool AutoForPlainRequests { get; set; }

    /// <summary>Formats to try, best first. Null means <see cref="DefaultPreference"/> (AVIF, then WebP). Falls back to the source format.</summary>
    public List<string>? Preference { get; set; }
}

/// <summary>
/// "Fast first, small later": answer with the fast encoder, then re-encode WebP/AVIF in the background at
/// these efforts and swap the cached file when it is smaller. Measured at equal quality on the Kodak photo set:
/// AVIF effort 4 is ~19 % smaller than effort 0; WebP effort 4 ~21 % smaller than effort 0.
/// </summary>
public sealed class OptimizeOptions
{
    /// <summary>On by default; it only does work for WebP/AVIF entries whose target effort is higher than the first encode.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Target libwebp effort (0–6). Equal to the default first-encode effort, so WebP is not re-encoded unless you lower <see cref="DefaultsOptions.WebpEffort"/>.</summary>
    public int WebpEffort { get; set; } = 4;

    /// <summary>Target AVIF effort (0–9). 4 takes ~5–7 s per 1920 px image on one core.</summary>
    public int AvifEffort { get; set; } = 4;

    /// <summary>Background re-encodes running at once (each also takes a processing slot). Requires a restart.</summary>
    public int MaxConcurrent { get; set; } = 1;

    /// <summary>Max queued jobs; extra ones are dropped and offered again on a later cache hit. Requires a restart.</summary>
    public int QueueLimit { get; set; } = 10_000;

    /// <summary>Time limit for one background re-encode (high efforts are slow). On timeout the fast encode is kept.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Only replace the cached file when the new one is at least this much smaller.</summary>
    public double MinSavingsPercent { get; set; } = 1;
}

/// <summary>
/// Master copies: the first resize of a large original also stores a high-quality downscaled copy,
/// and later sizes of the same image are made from it instead of decoding the full original again.
/// Measured ~2x faster for every size after the first on 24 MP photos; costs one extra file per original
/// and a small, normally invisible quality loss (an extra JPEG generation at <see cref="Quality"/>).
/// </summary>
public sealed class MasterOptions
{
    public bool Enabled { get; set; }

    /// <summary>Longest side of the master copy. Requests larger than the master use the original.</summary>
    public int Size { get; set; } = 2560;

    /// <summary>JPEG quality of the master copy.</summary>
    public int Quality { get; set; } = 95;

    /// <summary>Originals smaller than this (bytes) never get a master; they are already fast to decode.</summary>
    public long MinSourceBytes { get; set; } = 1_500_000;
}

public sealed class SourceOptions
{
    /// <summary>
    /// Folder that original images are read from. Relative paths resolve against the content root.
    /// Null means the web root (wwwroot). Requires a restart to change.
    /// </summary>
    public string? Root { get; set; }
}

public sealed class CacheOptions
{
    /// <summary>Where processed images are stored. Relative paths resolve against the content root.</summary>
    public string Path { get; set; } = "App_Data/bildverk-cache";

    /// <summary>How many folder levels the hash is split into, so no single folder grows huge.</summary>
    public int FolderDepth { get; set; } = 2;

    /// <summary>Number of hex characters of the SHA-256 cache key that are used (16–64; 16 = 64 bits).</summary>
    public int HashLength { get; set; } = 16;

    /// <summary>A cached image older than this is reprocessed on the next request and removed by cleanup.</summary>
    public TimeSpan MaxAge { get; set; } = TimeSpan.FromDays(365);

    /// <summary>
    /// Upper bound for the cache folder in megabytes (default 10 GB); least recently used entries are evicted first.
    /// 0 = unlimited, which lets anyone fill the disk with size variants unless URLs are signed or sizes restricted.
    /// </summary>
    public long MaxSizeMB { get; set; } = 10_240;

    /// <summary>How often the background cleanup runs. Zero disables it.</summary>
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromHours(1);
}

public sealed class BrowserOptions
{
    /// <summary>Cache-Control max-age sent to browsers and CDNs.</summary>
    public TimeSpan MaxAge { get; set; } = TimeSpan.FromDays(7);
}

public sealed class LimitsOptions
{
    /// <summary>Maximum output width. Requested widths are clamped to it, and sizes computed from the aspect ratio are scaled to fit.</summary>
    public int MaxWidth { get; set; } = 4000;

    /// <summary>Maximum output height (same rules as <see cref="MaxWidth"/>).</summary>
    public int MaxHeight { get; set; } = 4000;

    /// <summary>
    /// Optional fixed set of widths (like a srcset breakpoint list). When set, a requested width is snapped up to the
    /// nearest allowed one (or down to the largest), so at most this many width variants can exist per image.
    /// </summary>
    public List<int>? AllowedWidths { get; set; }

    /// <summary>Same as <see cref="AllowedWidths"/>, for heights.</summary>
    public List<int>? AllowedHeights { get; set; }

    public static readonly IReadOnlyList<string> DefaultInputFormats = ["jpeg", "png", "webp", "gif", "avif", "tiff"];

    /// <summary>
    /// Source formats that may be decoded: jpeg, png, webp, gif, avif (= heif), tiff. Null means
    /// <see cref="DefaultInputFormats"/>. The format is detected from the file's first bytes, not its extension,
    /// and anything else is rejected (415) before a decoder runs. Drop "tiff" if you don't need it: of the
    /// bundled codecs it has the longest security history.
    /// </summary>
    public List<string>? AllowedInputFormats { get; set; }

    /// <summary>
    /// Processing of one image is aborted (422) after this long, so a deliberately slow file (e.g. a JPEG with
    /// thousands of progressive scans) can't hold a worker. Applies to requests; see <see cref="OptimizeOptions.Timeout"/>.
    /// </summary>
    public TimeSpan ProcessingTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Source images with more pixels than this are rejected (decompression-bomb guard).</summary>
    public long MaxSourcePixels { get; set; } = 100_000_000;

    /// <summary>Source files larger than this are rejected.</summary>
    public long MaxSourceBytes { get; set; } = 100L * 1024 * 1024;

    /// <summary>How many images may be processed at the same time. Requires a restart to change.</summary>
    public int? MaxConcurrentProcessing { get; set; }

    /// <summary><see cref="MaxConcurrentProcessing"/>, or the number of CPU cores when it is not set.</summary>
    internal int EffectiveMaxConcurrentProcessing => MaxConcurrentProcessing ?? Environment.ProcessorCount;

    /// <summary>
    /// libvips worker threads per image. 1 (default) gives the best server throughput: parallelism comes
    /// from processing several images at once instead of oversubscribing the CPU. Requires a restart to change.
    /// </summary>
    public int ThreadsPerImage { get; set; } = 1;
}

public sealed class DefaultsOptions
{
    /// <summary>Quality used for JPEG, WebP and AVIF when the request has no quality command.</summary>
    public int Quality { get; set; } = 80;

    /// <summary>
    /// Progressive JPEG (default on). Measured at equal visual quality (SSIM): ~3 % fewer bytes than baseline,
    /// for ~10–20 ms extra encode time once per cached image. Also renders gradually in browsers.
    /// </summary>
    public bool ProgressiveJpeg { get; set; } = true;

    /// <summary>
    /// libwebp effort 0–6 (default 4, libwebp's own default). Measured at equal visual quality on the Kodak photo set:
    /// effort 0 needs ~27 % more bytes than 4 and effort 2 ~6.5 % more; 6 saves nothing over 4.
    /// Lower it (with <see cref="OptimizeOptions.WebpEffort"/> = 4) for faster first responses.
    /// </summary>
    public int WebpEffort { get; set; } = 4;

    /// <summary>
    /// AVIF (aom) effort 0–9 for the first response. 0 is fast (~105 ms at 1920 px); 4 is ~19 % smaller at equal
    /// quality but takes seconds, so by default the background optimizer upgrades AVIF to effort 4 after the fact.
    /// </summary>
    public int AvifEffort { get; set; }

    internal Processing.EncoderSettings Encoder => new(ProgressiveJpeg, WebpEffort, AvifEffort);
}

public sealed class SecurityOptions
{
    /// <summary>
    /// When set, every processed request must carry a valid &amp;hmac= signature
    /// (see <see cref="Security.IBildverkUrlSigner"/>). Unsigned requests get 403.
    /// </summary>
    public string? HmacKey { get; set; }
}
