# Bildverk

Self-hosted, open-source image processing middleware for ASP.NET Core. Resize, crop, pad and convert images on the fly from the URL, with a disk cache you configure in `appsettings.json`.

```html
<img src="/images/hero.jpg?width=1920&height=600&rmode=crop&quality=80" />
```

It works in any ASP.NET Core app: Blazor (Server, Web App, or WASM *hosted*), MVC, Razor Pages and minimal APIs. It uses the same query syntax as ImageSharp.Web and Umbraco, so existing URLs keep working. It runs on [libvips](https://www.libvips.org/) through NetVips.

**License:** MIT. The bundled native libvips build is LGPL-3.0 (see [Dependencies](#dependencies)). It ships as a separate, replaceable library file, which is what the LGPL requires, so it is fine for closed-source and commercial apps.

## Setup

```csharp
builder.Services.AddBildverk();   // binds the "Bildverk" section

var app = builder.Build();
app.UseBildverk();                // BEFORE UseStaticFiles / MapStaticAssets
app.UseStaticFiles();
```

Requests with no image commands go straight through to static files, so the original image is served as normal.

## Commands

| Parameter | Values | |
|---|---|---|
| `width`, `height` | pixels | Clamped to `Limits.MaxWidth/MaxHeight` |
| `rmode` | `max` (default), `crop`, `pad`, `boxpad`, `min`, `stretch` | |
| `ranchor` | `center`, `top`, `bottom`, `left`, `right`, `topleft`, `topright`, `bottomleft`, `bottomright` | Crop/pad anchor |
| `rxy` | `0.71,0.28` | Focal point (0–1) for `crop`; overrides `ranchor` |
| `quality` | 1–100 | JPEG, WebP, AVIF |
| `format` | `jpg`, `png`, `webp`, `avif`, `gif`, `tiff`, `auto` | `auto` = best format the browser accepts |
| `bgcolor` | `fff`, `ff0000`, `ff000080`, `white`, `transparent` | Padding and transparency fill |

- **max:** fits inside the box and keeps the aspect ratio.
- **crop:** fills the box and cuts off whatever overflows.
- **pad:** fits inside the box and fills the rest with `bgcolor`.
- **boxpad:** like pad, but never upscales.
- **min:** the shortest side reaches the box; never upscales.
- **stretch:** gives exactly the box size and ignores the aspect ratio.

EXIF orientation is always applied, and metadata is stripped.

## appsettings.json

All keys are optional. These are the defaults:

```json
"Bildverk": {
  "Extensions": [".jpg", ".jpeg", ".png", ".webp", ".gif", ".avif", ".tif", ".tiff"],
  "Source": {
    "Root": null                          // null = wwwroot; else a folder (relative = content root)
  },
  "Cache": {
    "Path": "App_Data/bildverk-cache",    // where processed images go; absolute paths work too, e.g. "D:/imagecache"
    "FolderDepth": 2,                     // hash split into sub-folders: 3/f/3fa9c01b...
    "HashLength": 16,                     // hex chars of the SHA-256 key (8–64)
    "MaxAge": "365.00:00:00",             // older entries are reprocessed and cleaned up
    "MaxSizeMB": 10240,                   // LRU eviction down to 90 % when exceeded; 0 = unlimited
    "CleanupInterval": "01:00:00"         // background cleanup; "00:00:00" disables it
  },
  "Browser": {
    "MaxAge": "7.00:00:00"                // Cache-Control: public, max-age=...
  },
  "Limits": {
    "MaxWidth": 4000,
    "MaxHeight": 4000,
    "AllowedWidths": null,                // e.g. [320, 640, 960, 1280, 1920]: requests snap up to these
    "AllowedHeights": null,
    "MaxSourcePixels": 100000000,         // decompression-bomb guard → 413
    "AllowedInputFormats": null,          // default ["jpeg","png","webp","gif","avif","tiff"]; detected from file bytes
    "MaxSourceBytes": 104857600,
    "MaxConcurrentProcessing": 24,        // default: number of CPU cores
    "ThreadsPerImage": 1                  // libvips threads per image; 1 = best server throughput
  },
  "Defaults": {
    "Quality": 80,
    "ProgressiveJpeg": true,              // ~3 % smaller at equal quality; ~10-20 ms extra encode, once
    "WebpEffort": 4,                      // 0-6; 0 is ~3x faster but needs ~27 % more bytes
    "AvifEffort": 0                       // 0-9; first response fast, the optimizer upgrades it later
  },
  "Formats": {
    "AutoByDefault": false,               // true = every request without format= gets AVIF/WebP if the browser accepts it
    "AutoForPlainRequests": false,        // true = even /img/photo.jpg (no query) is converted
    "Preference": ["avif", "webp"]        // best first; falls back to the source format
  },
  "Optimize": {
    "Enabled": true,                      // background re-encode at higher effort, swap when smaller
    "WebpEffort": 4,                      // only does work if higher than Defaults.WebpEffort
    "AvifEffort": 4,                      // ~19 % smaller than effort 0, seconds per image, so in the background
    "MaxConcurrent": 1,
    "QueueLimit": 10000,
    "MinSavingsPercent": 1
  },
  "Masters": {
    "Enabled": false,                     // see "Master copies" below
    "Size": 2560,
    "Quality": 95,
    "MinSourceBytes": 1500000
  },
  "Security": {
    "HmacKey": null                       // set it, and only signed URLs are processed
  }
}
```

Changes to `appsettings.json` apply without a restart, except for `Source.Root`, `Limits.MaxConcurrentProcessing` and `Limits.ThreadsPerImage`. Any key can also be set through environment variables, e.g. `Bildverk__Cache__Path=/var/cache/images`.

## Automatic formats

AVIF and WebP need about 30–45 % fewer bytes than JPEG for the same visual quality (see below). There are three ways to turn that on:

| | Who decides | Example |
|---|---|---|
| `?format=auto` in the URL | The page author, per image | `<img src="/img/a.jpg?width=800&format=auto">` |
| `Formats:AutoByDefault` | The site owner, for every resized image | `?width=800` → AVIF, WebP or JPEG |
| `Formats:AutoForPlainRequests` | The site owner, for every image | `/img/a.jpg` → AVIF, WebP or JPEG |

The browser's `Accept` header decides which format it gets. Only explicit `image/avif` and `image/webp` count; `*/*` does not. The first match in `Formats:Preference` wins, and if nothing matches the source format is used.

- **CDN-safe:** responses carry `Vary: Accept`, so a CDN keeps the formats apart. That includes the JPEG fallback and plain files passed through to static files.
- **Explicit formats win:** an explicit `format=webp` or `format=png` always overrides automatic selection.
- **GIFs are never converted** automatically, because animation would be lost.
- **One cache entry per format.** `format=auto` that resolves to WebP shares its entry with `format=webp`.

## Optimize in the background

High encoder effort makes much smaller files but is slow. AVIF effort 4 takes seconds per image, so the first request is answered with the fast setting and the background optimizer re-encodes the cached file afterwards.

- **Replacement:** the cached file is replaced only when the new one is at least `MinSavingsPercent` smaller, and each entry is optimized once.
- **Browsers:** the ETag changes (`…-o`), so browsers revalidate and get the smaller file.
- **CPU:** `MaxConcurrent` (default 1) background jobs run at once, and each also takes a normal processing slot, so requests stay responsive.
- **Queue:** jobs dropped because the queue is full are offered again on a later cache hit.
- **Your choice of trade-off:**
  - Fastest first response: `Defaults:WebpEffort = 0` with `Optimize:WebpEffort = 4`.
  - Smallest files without background work: `Optimize:Enabled = false` with `Defaults:AvifEffort = 4`.

## How caching works

```
GET /images/hero.jpg?width=1920&height=600&rmode=crop
 │
 ├─ no known commands? ─────────────► next middleware (static file, original)
 ├─ HmacKey set and signature bad? ─► 403
 ├─ original missing? ──────────────► next middleware (404)
 │
 ├─ key = SHA-256("v1|/images/hero.jpg?format=jpg&height=600&quality=80&rmode=crop&width=1920")
 │        (commands sorted, clamped, defaults filled in; unknown params dropped)
 │
 ├─ {Cache.Path}/8/8/884388bd3be86702 (+ .json) exists,
 │  same source mtime+size, younger than MaxAge? ──► serve it (ETag, 304, SendFile)
 │
 └─ lock(key) → re-check → process with libvips (max N at once) → write to temp + rename → serve
```

- **Cache entries.** Equivalent URLs share one entry, whether the parameters are in a different order or case, have extra `utm=` parameters, or set a quality equal to the default.
- **One processing run per image.** Twelve simultaneous requests for an uncached image cause one processing run; the others wait and are served from the cache.
- **Updated originals.** Replacing an original image changes its modified time, so the next request reprocesses it.
- **Atomic writes.** Files are written to a temp file and renamed, so a crash never leaves a half-written image to be served.
- **ETags.** The ETag is built from the cache key and the source version, so it stays the same across servers and cache rebuilds. That makes it CDN-friendly.

## Performance vs ImageSharp.Web

Measured 2026-10-15 with `bench/` on a 24-thread Windows desktop running .NET 10. Both hosts use their default settings, the same 8 high-detail photos (four 4 MP and four 24 MP), and loopback HTTP. ImageSharp.Web 3.2.0 was used, because 3.3.0 and later refuse to build without a Six Labors licence key.

| Test | Bildverk | Bildverk + Masters | ImageSharp.Web |
|---|---|---|---|
| Hero crop 1920×600, 4 MP source | 40 ms | 41 ms | 38 ms |
| Hero crop 1920×600, 24 MP source | **93 ms** | 164 ms (creates the master) | 149 ms |
| 300×300 thumbnail, 24 MP source | 60 ms | **32 ms** | 119 ms |
| 800 px WebP, 4 MP source | **74 ms** | 75 ms | 205 ms |
| 800 px WebP, 24 MP source | 94 ms | **64 ms** | 252 ms |
| srcset, 5 sizes of a 4 MP photo (per image) | **45 ms** | 47 ms | 46 ms |
| srcset, 5 sizes of a 24 MP photo (per image) | 69 ms | **41 ms** | 121 ms |
| Burst: 96 uncached URLs, 32 parallel | 153 images/s | **195 images/s** | 126 images/s |
| Peak RAM during burst | 723 MB | **520 MB** | ~2 060 MB |
| Cached requests, 64 parallel | ~14 100 req/s | ~15 100 req/s | ~11 400 req/s |

The defaults favour bandwidth (progressive JPEG, WebP effort 4). That is why JPEG from small originals is about even with ImageSharp on speed while producing smaller files, as the next section shows.

Cached requests run at the same speed as plain `UseStaticFiles` serving the same bytes. There is nothing left to gain there in-process.

Run it yourself: put JPEGs in `bench/images/`, build `bench/*Host` with `-c Release`, then run `dotnet bench/LoadGen/bin/Release/net10.0/LoadGen.dll bench [Bildverk|ImageSharp.Web]`. `bench/Profile` times each processing stage.

### File size at equal quality

Comparing files at the same `quality=` number is misleading, because every encoder and setting looks different at a given Q. `bench/Codecs` therefore measures **the bytes needed to reach the same visual quality**. It uses SSIM against the uncompressed image, with the target set to what our JPEG reaches at q80. Every encoder gets identical pixels.

The set is the 24 Kodak test photos at 768 px and 400 px. Download them to `bench/kodak/` from r0k.us/graphics/kodak, then run `dotnet bench/Codecs/bin/Release/net10.0/Codecs.dll bench/kodak kodim01.png …` with `CODEC_WIDTHS=768,400`.

| Encoder / setting | Bytes at equal quality | Encode time |
|---|---|---|
| JPEG baseline | 100 % | 1× |
| **JPEG progressive (default)** | **97.7 %** | 2.7× |
| ImageSharp JPEG | 101.3 % | 1.0× |
| WebP effort 0 | 85.8 % | 3.4× |
| WebP effort 2 | 72.1 % | 5.3× |
| **WebP effort 4 (default)** | **67.7 %** | 11.5× |
| WebP effort 6 | 67.6 % | 25.6× |
| ImageSharp WebP | 67.5 % | 36.3× |
| AVIF effort 0 (first response) | 69.8 % | 7.1× |
| AVIF effort 2 | 62.0 % | 37.6× |
| **AVIF effort 4 (after background optimize)** | **56.5 %** | 322× |

- **Effort matters a lot on normal photos.** On smooth content such as sky and product backgrounds, the low efforts are much worse. On very high-detail textures it barely matters: WebP effort 0 is within 1 % of effort 4 there. Measure on your own images if you tune it.
- **AVIF after optimization** needs about 44 % fewer bytes than JPEG and about 16 % fewer than WebP.

### Where the time goes, and the limits

- **JPEG decode is the floor.** Libvips already decodes at 1/2, 1/4 or 1/8 size for small outputs, but every byte of the file still has to be read once, single-threaded. That is about 40 ms for an 8 MB, 24 MP photo.
- **Master copies get around that floor** for every size after the first. They help when photos are large and many sizes are used, such as a srcset or a gallery plus thumbnails.
- **WebP and AVIF are limited by their encoders.** The defaults are the fastest effort levels, chosen from measurements.
- **Plumbing overhead is about 5–10 ms per cold image**, covering HTTP, the cache write, metadata and the header check.
- **Throughput:** each image runs on one libvips thread, with as many images in parallel as there are cores. Letting libvips also spread each image over all cores oversubscribed the CPU and cut burst throughput by 2.4×.

### Master copies

With `Masters:Enabled`, the first resize of a large original (long side ≥ 1.5 × `Size`) also stores a ~2560 px JPEG at quality 95. Later sizes are cut from that copy.

- **Cost:** the first request for each large photo takes about 2× longer, and there's one extra cache file per original (~3–4 MB).
- **Quality:** there's a small loss from the extra JPEG generation, measured at 38–46 dB PSNR against direct resizing on worst-case high-detail textures.
- **Edge cases:** proportional sizes may differ by 1 px from direct resizing because of rounding. Requests larger than the master always use the original.

## Scaling

- **One server:** processing runs on all cores, and a cache hit is a `SendFile` straight from disk.
- **Several servers:** put a CDN in front, which becomes the real cache, or share `Cache.Path` on a network share. ETags are deterministic, so every server returns the same ETag for the same image.
- **Bursts:** `MaxConcurrentProcessing` queues work instead of letting memory explode, and per-URL locking means a burst for one new image is processed once.

## Signed URLs

Set `Security:HmacKey`, then sign URLs on the server:

```razor
@inject Bildverk.Security.IBildverkUrlSigner Signer
<img src="@Signer.Sign("/images/hero.jpg?width=800&height=400&rmode=crop")" />
```

Unsigned or tampered requests get a 403. Originals without commands are still served.

## Replacing parts

Register your own implementation before or after `AddBildverk()`:

| Interface | Default |
|---|---|
| `IImageSource` | `FileSystemImageSource` (wwwroot or `Source.Root`) |
| `IImageCache` | `FileSystemImageCache` |
| `IImageProcessor` | `VipsImageProcessor` |

## Security

Every item below has a test in `tests/Bildverk.Tests/SecurityTests.cs`, and the processing path is fuzzed with `bench/Fuzz`.

**Untrusted files**
- The format is detected from the file's first bytes, not its extension. Only `Limits:AllowedInputFormats` reach a decoder: JPEG, PNG, WebP, GIF, AVIF and TIFF by default. An SVG renamed to `.jpg` is rejected (415) before any parser touches it, and libvips' own "untrusted" loaders are blocked as well.
- Drop `tiff` from the list if you don't need it: of the bundled codecs, TIFF has the longest security history.
- Source pixel count and file size are checked from the header, before any pixels are decoded. Responses: decompression bomb → 413, corrupt image → 422, not an image → 415.
- `Limits:ProcessingTimeout` (30 s) aborts work midway, so a deliberately slow file can't hold a worker. The background optimizer has its own `Optimize:Timeout`.

**Output size**
- Every output fits `MaxWidth × MaxHeight`, including sizes computed from the aspect ratio. Without this, a 500 000 × 60 image with `height=350` would become a billion pixels; the fuzzer found exactly that.
- Crops and pads that would need an enormous intermediate image are rejected (422).

**Paths**
- Originals are read through `IFileProvider`, so `../`, encoded `%2e%2e`, backslashes, absolute and UNC paths, NTFS streams and device names (`con.jpg`) are not served. Hidden files are not served either.
- Control characters in paths → 400.
- Cache metadata can only name files in its own folder.

**The cache can't damage anything**
- The app refuses to start if `Cache:Path` is, contains or sits inside the content root, web root or image source.
- The cleanup only ever deletes files named exactly like its own (`{hex key}.json`, `{hex key}.{hex version}`, their `.tmp` files) and its own one-character shard folders. Anything else in the folder is left alone.
- A cache hit must match the full request description, not just its hash, so even a hash collision can't serve the wrong image. `HashLength` is at least 16 (64 bits).
- Every write goes to a new file and is swapped in atomically. A file being downloaded is never overwritten, and a crash never leaves a half-written image to be served.

**Abuse: endless size variants filling CPU and disk.** Pick one or more:
- `Security:HmacKey` (at least 16 characters, 32+ recommended): only URLs signed by your server are processed. This is the strongest option.
- `Limits:AllowedWidths` / `AllowedHeights`: requests snap to a fixed list, so only a few variants can exist per image.
- `Cache:MaxSizeMB` (default 10 GB): the disk use stays bounded no matter what.
- CPU is always bounded (`MaxConcurrentProcessing`, timeouts).
- At startup, Bildverk logs a warning when the configuration leaves this open.

**Privacy**
- EXIF, XMP and other metadata (GPS position, camera, author) are stripped from every output.

**Responses and logs**
- Responses carry `X-Content-Type-Options: nosniff`, the exact `Content-Type`, and `Vary: Accept` whenever the format was negotiated.
- Logs only contain escaped paths, so a URL can't forge log lines.

**Code**
- Built with the .NET security and reliability analyzers set to "all", and warnings fail the build.

## Dependencies

There are exactly two packages, both maintained by the libvips project:

| Package | What | Licence |
|---|---|---|
| `NetVips` | Thin C# binding to libvips | MIT |
| `NetVips.Native` | One prebuilt native library per platform (~18 MB): Windows, Linux (glibc and musl), macOS, on x64 and ARM64 | LGPL-3.0 (libvips) plus the bundled libraries' own licences |

The native library bundles the codecs: mozjpeg, libpng, libwebp, libheif with aom (AVIF), libtiff and cgif/libnsgif. These are the same C libraries browsers and image services use. They are fuzzed continuously by Google OSS-Fuzz and receive security fixes quickly. The build also contains libraries Bildverk never calls (librsvg, pango/cairo for text, libxml2, libarchive); format sniffing keeps untrusted input away from them.

**Why not our own code?** The managed part (HTTP, caching, negotiation, geometry, limits) is already our own code. The codecs are another matter. A JPEG/PNG/WebP/AVIF decoder written from scratch would be slower than these SIMD-tuned libraries and far *less* safe: codecs are where image-processing vulnerabilities live, and these have years of fuzzing behind them. The realistic hardening steps are:

1. Keep `NetVips.Native` up to date. The release pipeline should run Dependabot or Renovate, because codec fixes ship there.
2. Restrict `AllowedInputFormats` to what you actually serve.
3. Optionally, a custom slim libvips build with only the six codecs. That means a smaller attack surface and a smaller download, but we'd have to build and patch it ourselves.

## Known limits

- Animated GIF and WebP output only the first frame.
- With several servers, use a shared cache path (a network share) or put a CDN in front. A blob-storage cache isn't built yet.

## Development

```
dotnet test --project tests/Bildverk.Tests
dotnet run --project samples/Bildverk.Sample --launch-profile http   # http://localhost:5026
```
