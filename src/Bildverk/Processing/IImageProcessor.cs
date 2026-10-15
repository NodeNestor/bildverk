using Bildverk.Commands;

namespace Bildverk.Processing;

public sealed record ProcessedImage(byte[] Data, string ContentType, int Width = 0, int Height = 0);

/// <summary>Encoder knobs that change the output bytes (and therefore are part of the cache key).</summary>
public sealed record EncoderSettings(bool ProgressiveJpeg = false, int WebpEffort = 0, int AvifEffort = 0)
{
    internal string KeyPart(OutputFormat format) => format switch
    {
        OutputFormat.Jpeg => ProgressiveJpeg ? "p" : "",
        OutputFormat.WebP => $"e{WebpEffort}",
        OutputFormat.Avif => $"e{AvifEffort}",
        _ => "",
    };
}

/// <summary>What the processor must do: the request's commands plus the resolved output format and quality.</summary>
/// <param name="Timeout">Processing is aborted (422) after this long. Zero or negative = no limit.</param>
public sealed record ProcessingRequest(ImageCommands Commands, OutputFormat Format, int Quality, LimitsOptions Limits, EncoderSettings? Encoder = null, TimeSpan Timeout = default);

public interface IImageProcessor
{
    Task<ProcessedImage> ProcessAsync(Stream source, ProcessingRequest request, CancellationToken cancellationToken);

    /// <summary>Pixel size from the header only, or null when unknown. Used to decide whether a master copy pays off.</summary>
    Task<(int Width, int Height)?> IdentifyAsync(Stream source, CancellationToken cancellationToken) => Task.FromResult<(int, int)?>(null);
}

/// <summary>Thrown for input the client is responsible for (corrupt, too large, unsupported). Mapped to a 4xx.</summary>
public sealed class ImageProcessingException(int statusCode, string message, Exception? inner = null) : Exception(message, inner)
{
    public int StatusCode { get; } = statusCode;
}

public static class ContentTypes
{
    public static string For(OutputFormat format) => format switch
    {
        OutputFormat.Jpeg => "image/jpeg",
        OutputFormat.Png => "image/png",
        OutputFormat.WebP => "image/webp",
        OutputFormat.Gif => "image/gif",
        OutputFormat.Avif => "image/avif",
        OutputFormat.Tiff => "image/tiff",
        _ => "application/octet-stream",
    };

    public static bool SupportsAlpha(OutputFormat format) => format is not OutputFormat.Jpeg;
}
