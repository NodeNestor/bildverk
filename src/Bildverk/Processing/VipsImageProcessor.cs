using Bildverk.Commands;
using Microsoft.Extensions.Options;
using NetVips;
using VipsImage = NetVips.Image;

namespace Bildverk.Processing;

/// <summary>
/// libvips-based processor. Uses vips thumbnail, which shrinks JPEG/WebP while decoding,
/// so a 6000×4000 photo resized to 400 px never gets fully decoded.
/// </summary>
public sealed class VipsImageProcessor : IImageProcessor
{
    /// <summary>
    /// Input format names usable in <see cref="LimitsOptions.AllowedInputFormats"/> → libvips loader class
    /// (as returned by format sniffing, e.g. "VipsForeignLoadJpegBuffer").
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> InputLoaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["jpeg"] = "VipsForeignLoadJpeg",
        ["png"] = "VipsForeignLoadPng",
        ["webp"] = "VipsForeignLoadWebp",
        ["gif"] = "VipsForeignLoadNsgif",
        ["avif"] = "VipsForeignLoadHeif",
        ["heif"] = "VipsForeignLoadHeif",
        ["tiff"] = "VipsForeignLoadTiff",
    };

    static VipsImageProcessor()
    {
        // Blocks loaders libvips itself marks as unsafe for untrusted input.
        global::NetVips.NetVips.BlockUntrusted = true;
    }

    public VipsImageProcessor() { }

    public VipsImageProcessor(IOptions<BildverkOptions> options)
    {
        // Process-wide libvips setting.
        global::NetVips.NetVips.Concurrency = Math.Max(1, options.Value.Limits.ThreadsPerImage);
    }

    public Task<ProcessedImage> ProcessAsync(Stream source, ProcessingRequest request, CancellationToken cancellationToken) =>
        Task.Run(async () =>
        {
            var bytes = await ReadAllAsync(source, request.Limits.MaxSourceBytes, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return Process(bytes, request, cancellationToken);
        }, cancellationToken);

    public Task<(int Width, int Height)?> IdentifyAsync(Stream source, CancellationToken cancellationToken) =>
        Task.Run<(int Width, int Height)?>(() =>
        {
            try
            {
                // Lazy: libvips reads only as much of the stream as the header needs,
                // and only after the format was sniffed from its first bytes and is allowed.
                if (!source.CanSeek) return null;
                var start = source.Position;
                if (!IsAllowed(VipsImage.FindLoadStream(source), null)) return null;
                source.Position = start;
                using var header = VipsImage.NewFromStream(source, access: Enums.Access.Sequential);
                return (header.Width, header.Height);
            }
            catch (VipsException)
            {
                return null;
            }
        }, cancellationToken);

    internal static ProcessedImage Process(byte[] bytes, ProcessingRequest request, CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (request.Timeout > TimeSpan.Zero) deadline.CancelAfter(request.Timeout);
        try
        {
            var result = ProcessCore(bytes, request, deadline.Token);

            // The kill flag stops decoding and resizing, but an encoder that is already running finishes its image.
            // Over the limit is over the limit: discard the result so the rule is the same for every format.
            if (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                throw new ImageProcessingException(422, $"Processing took longer than the {request.Timeout.TotalSeconds:0.#} s limit.");
            return result;
        }
        catch (VipsException ex) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new ImageProcessingException(422, $"Processing took longer than the {request.Timeout.TotalSeconds:0.#} s limit.", ex);
        }
        catch (VipsException ex)
        {
            throw new ImageProcessingException(422, "The source image could not be decoded or processed.", ex);
        }
    }

    private static ProcessedImage ProcessCore(byte[] bytes, ProcessingRequest request, CancellationToken deadline)
    {
        var c = request.Commands;

        // Identify the format from its magic bytes only. Anything not allowed is rejected before any
        // decoder (or its header parser) runs, whatever the file extension claims: no SVG, PDF, XML ...
        var loader = VipsImage.FindLoadBuffer(bytes);
        if (!IsAllowed(loader, request.Limits.AllowedInputFormats))
            throw new ImageProcessingException(415, $"Unsupported or disallowed image type '{loader ?? "unknown"}'.");

        // Header only: cheap, and lets us reject oversized files before decoding pixels.
        int width, height;
        using (var header = VipsImage.NewFromBuffer(bytes))
        {
            if ((long)header.Width * header.Height > request.Limits.MaxSourcePixels)
                throw new ImageProcessingException(413, "The source image has too many pixels.");

            var orientation = header.Contains("orientation") ? (int)header.Get("orientation") : 1;
            (width, height) = orientation is >= 5 and <= 8 ? (header.Height, header.Width) : (header.Width, header.Height);
        }

        var plan = ResizePlan.Limit(ResizePlan.Create(width, height, c), request.Limits.MaxWidth, request.Limits.MaxHeight)
            ?? throw new ImageProcessingException(422, "The request would produce an image larger than the configured limits.");

        var image = plan.ScaledWidth == width && plan.ScaledHeight == height
            ? VipsImage.NewFromBuffer(bytes).Autorot()
            : VipsImage.ThumbnailBuffer(bytes, plan.ScaledWidth, height: plan.ScaledHeight, size: Enums.Size.Force);

        try
        {
            image = ToSrgb(image);

            if (plan.Crop is { } crop)
            {
                // Guard against thumbnail rounding one pixel differently from the plan.
                var w = Math.Min(crop.Width, image.Width);
                var h = Math.Min(crop.Height, image.Height);
                image = Swap(image, image.Crop(Math.Clamp(crop.Left, 0, image.Width - w), Math.Clamp(crop.Top, 0, image.Height - h), w, h));
            }

            var background = c.Background ?? (ContentTypes.SupportsAlpha(request.Format) ? Rgba.Transparent : Rgba.White);

            if (plan.Pad is { } pad)
            {
                if (background.A < 255 && !image.HasAlpha()) image = Swap(image, image.Bandjoin(255));
                image = Swap(image, image.Embed(pad.Left, pad.Top, pad.Width, pad.Height,
                    extend: Enums.Extend.Background, background: Color(background, image.HasAlpha())));
            }

            // bgcolor fills transparency; JPEG cannot hold alpha at all.
            if (image.HasAlpha() && (c.Background is { A: 255 } || !ContentTypes.SupportsAlpha(request.Format)))
            {
                var flat = c.Background is { A: 255 } solid ? solid : Rgba.White;
                image = Swap(image, image.Flatten(background: Color(flat, alpha: false)));
            }

            // All the work (decode, resize, encode) happens lazily inside this write; the kill flag stops it midway.
            image.SetProgress(NoProgress.Instance, deadline);
            var data = image.WriteToBuffer(SaveOptions(request.Format, request.Quality, request.Encoder ?? new EncoderSettings()));
            return new ProcessedImage(data, ContentTypes.For(request.Format), image.Width, image.Height);
        }
        finally
        {
            image.Dispose();
        }
    }

    private sealed class NoProgress : IProgress<int>
    {
        public static readonly NoProgress Instance = new();
        public void Report(int value) { }
    }

    private static bool IsAllowed(string? loader, List<string>? allowedFormats) =>
        loader is not null
        && (allowedFormats ?? LimitsOptions.DefaultInputFormats)
            .Any(f => InputLoaders.TryGetValue(f, out var prefix) && loader.StartsWith(prefix, StringComparison.Ordinal));

    private static VipsImage ToSrgb(VipsImage image)
    {
        if (image.Contains("icc-profile-data"))
        {
            try
            {
                image = Swap(image, image.IccTransform("srgb", embedded: true, intent: Enums.Intent.Perceptual));
            }
            catch (VipsException)
            {
                // Broken profile: fall back to plain colourspace conversion below.
            }
        }

        if (image.Interpretation != Enums.Interpretation.Srgb)
            image = Swap(image, image.Colourspace(Enums.Interpretation.Srgb));

        // Encoders only ever see RGB or RGBA. Odd channel counts (e.g. multi-band TIFFs) have crashed
        // libvips encoders before (CVE-2025-29769); extra channels are dropped here as a second fence.
        if (image.Bands > 4) image = Swap(image, image.ExtractBand(0, n: 4));
        if (image.Bands is not (3 or 4))
            throw new ImageProcessingException(422, $"Unsupported channel layout ({image.Bands} channels).");

        return image.Format == Enums.BandFormat.Uchar ? image : Swap(image, image.Cast(Enums.BandFormat.Uchar));
    }

    private static string SaveOptions(OutputFormat format, int quality, EncoderSettings e) => format switch
    {
        OutputFormat.Jpeg => $".jpg[Q={quality},optimize_coding=true,interlace={(e.ProgressiveJpeg ? "true" : "false")},keep=none]",
        OutputFormat.Png => ".png[compression=6,keep=none]",
        OutputFormat.WebP => $".webp[Q={quality},effort={Math.Clamp(e.WebpEffort, 0, 6)},keep=none]",
        OutputFormat.Gif => ".gif[keep=none]",
        OutputFormat.Avif => $".avif[Q={quality},effort={Math.Clamp(e.AvifEffort, 0, 9)},keep=none]",
        OutputFormat.Tiff => ".tif[compression=deflate,keep=none]",
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    private static double[] Color(Rgba c, bool alpha) => alpha ? [c.R, c.G, c.B, c.A] : [c.R, c.G, c.B];

    /// <summary>Disposes the previous image in a pipeline step and returns the new one.</summary>
    private static VipsImage Swap(VipsImage old, VipsImage next)
    {
        if (!ReferenceEquals(old, next)) old.Dispose();
        return next;
    }

    private static async Task<byte[]> ReadAllAsync(Stream source, long maxBytes, CancellationToken ct)
    {
        if (source.CanSeek && source.Length > maxBytes)
            throw new ImageProcessingException(413, "The source image file is too large.");

        if (source.CanSeek)
        {
            // Known length: read straight into the final array, no intermediate copy.
            var exact = new byte[source.Length - source.Position];
            await source.ReadExactlyAsync(exact, ct);
            return exact;
        }

        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            if (ms.Length + read > maxBytes) throw new ImageProcessingException(413, "The source image file is too large.");
            ms.Write(buffer, 0, read);
        }
        return ms.ToArray();
    }
}
