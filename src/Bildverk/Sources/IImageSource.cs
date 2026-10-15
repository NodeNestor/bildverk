namespace Bildverk.Sources;

/// <summary>An original image found by an <see cref="IImageSource"/>.</summary>
public sealed record SourceImage(DateTimeOffset LastModifiedUtc, long Length, Func<CancellationToken, Task<Stream>> OpenReadAsync);

/// <summary>Where original images come from (disk, blob storage, ...).</summary>
public interface IImageSource
{
    /// <summary>Returns null when no image exists at <paramref name="path"/>.</summary>
    Task<SourceImage?> GetAsync(string path, CancellationToken cancellationToken);
}
