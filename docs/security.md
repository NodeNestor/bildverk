# Security

Bildverk decodes files and URLs from the internet, so it is built to treat both as hostile.

- **Tests:** every protection below has a test in [`tests/Bildverk.Tests/SecurityTests.cs`](../tests/Bildverk.Tests/SecurityTests.cs).
- **Fuzzing:** the processing path is fuzzed with corrupted images (`bench/Fuzz`, weekly in CI). About a million inputs so far, with no crashes.
- **Scanning:** the code is scanned by CodeQL and by the .NET security and reliability analyzers, and any warning fails the build.

To report a vulnerability, see [SECURITY.md](../SECURITY.md).

## Untrusted images

- **The format is detected from the file's bytes, not its name.** Only formats in `Limits:AllowedInputFormats` reach a decoder: JPEG, PNG, WebP, GIF, AVIF and TIFF by default. An SVG renamed to `.jpg` is rejected (415) before any parser sees it, and libvips' own "untrusted" loaders are blocked as well.
- **TIFF is opt-in.** It is rare on the web and was behind most libvips security fixes in 2025–2026, so it is off by default. To enable it, add it to `AllowedInputFormats` and `.tif` to `Extensions`.
- **Encoders only see RGB or RGBA.** Images with unusual channel counts, such as multi-band TIFFs, are normalized first. That kind of input once crashed a libvips encoder (CVE-2025-29769).
- **Size limits come first.** Pixel count and file size are checked from the header, before decoding. A decompression bomb gets 413, a corrupt image 422, and something that isn't an image 415.
- **Slow images are stopped.** Decoding and resizing are aborted after `Limits:ProcessingTimeout` (30 s), so a deliberately slow file can't hold a worker. An encoder that has already started finishes its current image, but the result is discarded (422). Background re-encodes have their own `Optimize:Timeout`.

## Output size

- **Every output fits within `MaxWidth × MaxHeight`.** This includes sizes computed from the aspect ratio: otherwise a 500 000 × 60 image requested with `height=350` would become a billion pixels. The fuzzer found exactly that, and it is now covered by tests.
- **Huge intermediate images are refused.** A crop or pad that would need an enormous intermediate image gets 422.

## Paths

- **Originals can't escape their folder.** They are read through `IFileProvider`, so none of these are served:
  - `../` and its encoded form `%2e%2e`
  - backslashes
  - absolute and UNC paths
  - NTFS streams (`a.jpg:x`)
  - device names (`con.jpg`)
  - hidden files
- **Control characters and backslashes** in a path get 400.
- **One cache entry per file.** Repeated slashes are collapsed and, on case-insensitive file systems (Windows, macOS), paths are compared without case. So `/a.jpg`, `/A.JPG` and `//a.jpg` can't be used to multiply variants.

## The cache

- **The cache folder can't overlap the app.** The app refuses to start if `Cache:Path` is, contains or is inside the content root, the web root or the image source folder.
- **The cleanup only deletes its own files.** It only ever removes files named exactly like the ones it writes, and its own one-character sub-folders. Anything else in the folder is left alone.
- **A cache hit must match the full request,** not just its hash, so even a deliberate hash collision can't serve the wrong image.
- **Writes are atomic.** Every write goes to a new file that is swapped in. A file being downloaded is never overwritten, and a crash never leaves a half-written image to be served.

## Abuse: endless size variants

Each different `width` is a new file and new processing work. Pick one or more of these:

- **`Security:HmacKey`:** only URLs signed by your server are processed. Use at least 16 characters, ideally 32 or more. This is the strongest option.
- **`Limits:AllowedWidths` / `AllowedHeights`:** requests snap to a fixed list, so only a few variants per image can exist.
- **`Cache:MaxSizeMB`** (default 10 GB) keeps disk use bounded in any case.
- **CPU is always bounded** (`MaxConcurrentProcessing` and the timeouts).
- **Waiting work is capped.** Beyond `Limits:MaxQueuedRequests`, requests for images not yet cached get 503 with `Retry-After` instead of piling up. Cached images are always served.
- **Startup warning:** Bildverk logs a warning when the configuration leaves this open.

## Custom image sources

Bildverk itself only reads local files. If you write an `IImageSource` that fetches originals over HTTP or from storage, the request path becomes input to that fetch, so:

- **Map paths to a fixed set of locations.** Never use the path as a URL or host name, or Bildverk becomes a proxy into your internal network. That is the server-side request forgery (SSRF) behind imgproxy CVE-2023-30019 and CVE-2025-24354.
- **Return `null` for anything unexpected.** Bildverk then answers 404.

## Privacy

EXIF, XMP and other metadata (GPS position, camera, author) are removed from every output.

## Responses and logs

- **Responses** carry `X-Content-Type-Options: nosniff`, the exact `Content-Type`, and `Vary: Accept` whenever the format was chosen from the `Accept` header.
- **Logs** only contain escaped paths, so a URL can't forge log lines.

## Dependencies

| Package | What | Licence |
|---|---|---|
| `NetVips` | C# binding to libvips | MIT |
| `NetVips.Native` | One prebuilt native library per platform (Windows, Linux glibc/musl, macOS; x64 and ARM64) | LGPL-3.0 (libvips) and the bundled libraries' own licences |

**Codecs.** The native library bundles mozjpeg, libpng, libwebp, libheif with aom (AVIF), libtiff and cgif/libnsgif. These are the same C libraries browsers use, fuzzed continuously by Google OSS-Fuzz. It also contains libraries Bildverk never calls (SVG, text rendering, XML, archives). Detecting the format from the file's bytes keeps untrusted input away from them.

**Security updates.** Codec fixes arrive through `NetVips.Native` updates. Dependabot proposes them in this repository, and in your own app you should keep the package current.

**Why not our own codecs?** The request handling, caching, format negotiation, geometry and limits are Bildverk's own code. Decoders written from scratch would be slower than these SIMD-tuned libraries, and much less safe: codecs are where image-processing vulnerabilities live, and these have years of fuzzing behind them.
