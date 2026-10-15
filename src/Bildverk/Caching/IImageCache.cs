namespace Bildverk.Caching;

/// <summary>
/// What is stored next to every cached image. <see cref="Canonical"/> is the full, unhashed description of the
/// request; a hit is only accepted when it matches, so a hash collision can never serve the wrong image.
/// </summary>
public sealed record CacheEntryMetadata(
    string ContentType,
    DateTimeOffset SourceLastModifiedUtc,
    long SourceLength,
    DateTimeOffset CachedAtUtc,
    int Width = 0,
    int Height = 0,
    bool Optimized = false,
    string? Canonical = null);

/// <summary>A cache hit.</summary>
/// <param name="PhysicalPath">Set when the entry is a local file, so it can be sent with zero-copy SendFile.</param>
public sealed record CachedImage(
    CacheEntryMetadata Metadata,
    long Length,
    DateTimeOffset LastModifiedUtc,
    string? PhysicalPath,
    Func<CancellationToken, Task<Stream>> OpenReadAsync);

public sealed record CleanupResult(int Expired, int Evicted, int Orphans, long BytesRemaining);

/// <summary>Storage for processed images, addressed by the hashed cache key.</summary>
public interface IImageCache
{
    Task<CachedImage?> GetAsync(string key, CancellationToken cancellationToken);

    Task<CachedImage> SetAsync(string key, ReadOnlyMemory<byte> data, CacheEntryMetadata metadata, CancellationToken cancellationToken);

    /// <summary>Removes expired entries and enforces the size limit. Called by the background cleanup.</summary>
    Task<CleanupResult> CleanupAsync(CancellationToken cancellationToken);
}
