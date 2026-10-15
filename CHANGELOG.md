# Changelog

## [0.1.0] - unreleased

First release.

**Images from the URL**
- `width`, `height`, `rmode` (max, crop, pad, boxpad, min, stretch), `ranchor`, `rxy` focal point, `quality`, `format`, `bgcolor`. These are compatible with the ImageSharp.Web and Umbraco syntax.

**Output formats**
- JPEG, PNG, WebP, AVIF, GIF and TIFF output, via libvips.
- `format=auto`, `Formats:AutoByDefault` and `Formats:AutoForPlainRequests` pick AVIF or WebP from the browser's `Accept` header, and responses carry `Vary: Accept`.
- Background optimizer: the first response uses the fast encoder, and a high-effort encode is swapped in later.

**Disk cache**
- Location, sharding, max age, size cap with LRU eviction and periodic cleanup, all set in appsettings.json.
- Per-URL locking, so a burst of requests for a new image processes it once.
- Atomic, versioned files.
- ETag and 304 support.

**Speed**
- Optional master copies make later sizes of large photos about 2× faster.

**Security**
- Signed URLs (HMAC).
- Allowed sizes and input formats.
- Format detection from file bytes.
- Processing time limit.
- Output size limits.
- Metadata stripping.
- Safe cache paths.
- Startup warnings for risky configurations.
- Queue limit for unprocessed images: 503 with `Retry-After` once it's full.
- Path spellings are normalized, so they can't multiply cache entries.
- Images reach the encoders only as RGB or RGBA.
- TIFF input is opt-in.

**Platforms**
- .NET 8 and .NET 10, on Windows, Linux and macOS.
