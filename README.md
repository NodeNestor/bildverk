# Bildverk

Self-hosted image processing middleware for ASP.NET Core. Resize, crop and convert images from the URL, cached on disk, configured in `appsettings.json`.

```html
<img src="/images/hero.jpg?width=1920&height=600&rmode=crop&format=auto" />
```

- **Any ASP.NET Core app:** Blazor (Server, Web App, hosted WASM), MVC, Razor Pages, minimal APIs.
- **Compatible URLs:** the same query syntax as ImageSharp.Web and Umbraco, so existing URLs keep working.
- **Formats:** JPEG, PNG, WebP, AVIF, GIF and TIFF, with automatic AVIF/WebP for browsers that support them.
- **Fast:** powered by [libvips](https://www.libvips.org/). See [performance](https://github.com/NodeNestor/bildverk/blob/main/docs/performance.md).
- **Open:** MIT licensed, .NET 8 and 10, Windows, Linux and macOS (x64 and ARM64).

## Install

Every [GitHub release](https://github.com/NodeNestor/bildverk/releases) has the NuGet package attached. Until it is on nuget.org:

```sh
mkdir packages   # put Bildverk.<version>.nupkg from the release here
dotnet nuget add source ./packages --name local
dotnet add package Bildverk
```

## Quick start

```csharp
builder.Services.AddBildverk();   // reads the "Bildverk" section of appsettings.json

var app = builder.Build();
app.UseBildverk();                // before UseStaticFiles / MapStaticAssets
app.UseStaticFiles();
```

Images in `wwwroot` can now be requested with commands. Requests without commands are served unchanged by static files.

```razor
<img src="/images/team.jpg?width=400&height=400&rmode=crop&rxy=0.5,0.3" alt="Team" />
```

## URL commands

| Command | Values | |
|---|---|---|
| `width`, `height` | pixels | Limited by `Limits:MaxWidth/MaxHeight` |
| `rmode` | `max` (default), `crop`, `pad`, `boxpad`, `min`, `stretch` | See below |
| `ranchor` | `center`, `top`, `bottom`, `left`, `right`, `topleft`, `topright`, `bottomleft`, `bottomright` | Where to crop or pad |
| `rxy` | `0.5,0.3` | Focal point for `crop` (0–1); overrides `ranchor` |
| `quality` | `1`–`100` | JPEG, WebP, AVIF. Default `Defaults:Quality` |
| `format` | `jpg`, `png`, `webp`, `avif`, `gif`, `tiff`, `auto` | `auto` = best format the browser accepts |
| `bgcolor` | `fff`, `ff0000`, `ff000080`, `white`, `transparent` | Fill for padding and transparency |

| `rmode` | Result |
|---|---|
| `max` | Fits inside the box, keeps the aspect ratio |
| `crop` | Fills the box, cuts off what overflows |
| `pad` | Fits inside the box, fills the rest with `bgcolor` |
| `boxpad` | Like `pad`, but never upscales |
| `min` | Shortest side reaches the box, never upscales |
| `stretch` | Exactly the box, aspect ratio ignored |

EXIF orientation is applied and metadata (GPS, camera, author) is removed from every output. Unknown parameters are ignored.

## Recipes

**Responsive images.** Restrict sizes to your breakpoints, so a page can't create endless variants:

```json
"Bildverk": { "Limits": { "AllowedWidths": [320, 640, 960, 1280, 1920] } }
```

```html
<img src="/img/a.jpg?width=960"
     srcset="/img/a.jpg?width=320 320w, /img/a.jpg?width=640 640w, /img/a.jpg?width=960 960w, /img/a.jpg?width=1920 1920w"
     sizes="(max-width: 960px) 100vw, 960px" />
```

Requested widths snap up to the nearest allowed one.

**AVIF/WebP automatically.** Use `format=auto` per image, or turn it on for everything:

```json
"Bildverk": { "Formats": { "AutoByDefault": true } }
```

The browser's `Accept` header decides the format: AVIF, then WebP, then the original format. Responses carry `Vary: Accept`, so CDNs cache each format separately. `AutoForPlainRequests` also converts URLs without a query (`/img/a.jpg`). GIFs are never converted automatically, because that would drop animation.

**Signed URLs.** Only URLs your server generated get processed:

```json
"Bildverk": { "Security": { "HmacKey": "<at least 32 random characters>" } }
```

```razor
@inject Bildverk.Security.IBildverkUrlSigner Signer
<img src="@Signer.Sign("/images/hero.jpg?width=800&height=400&rmode=crop")" />
```

Unsigned or modified URLs get 403. Originals without commands are still served.

**Store the cache elsewhere:**

```json
"Bildverk": { "Cache": { "Path": "D:/imagecache", "MaxSizeMB": 50000 } }
```

## Configuration

All keys are optional; these are the defaults. Settings apply without a restart, except those marked *restart*. Any key can also come from environment variables, for example `Bildverk__Cache__Path=/var/cache/images`.

```jsonc
"Bildverk": {
  "Extensions": [".jpg", ".jpeg", ".png", ".webp", ".gif", ".avif", ".tif", ".tiff"],
  "Source": {
    "Root": null                        // originals folder; null = wwwroot (restart)
  },
  "Cache": {
    "Path": "App_Data/bildverk-cache",  // relative to the app, or absolute ("D:/imagecache")
    "FolderDepth": 2,                   // sub-folder levels, keeps folders small
    "HashLength": 16,                   // 16–64 hex characters per file name
    "MaxAge": "365.00:00:00",           // older files are reprocessed and cleaned up
    "MaxSizeMB": 10240,                 // least recently used files are removed above this; 0 = no limit
    "CleanupInterval": "01:00:00"       // "00:00:00" turns the cleanup off
  },
  "Browser": {
    "MaxAge": "7.00:00:00"              // Cache-Control max-age for browsers and CDNs
  },
  "Limits": {
    "MaxWidth": 4000,                   // largest output, also for sizes computed from the aspect ratio
    "MaxHeight": 4000,
    "AllowedWidths": null,              // e.g. [320, 640, 1280]; requests snap up to these
    "AllowedHeights": null,
    "AllowedInputFormats": null,        // default ["jpeg", "png", "webp", "gif", "avif", "tiff"]
    "MaxSourcePixels": 100000000,       // larger originals are refused (413)
    "MaxSourceBytes": 104857600,
    "ProcessingTimeout": "00:00:30",    // one image may take at most this long (422)
    "MaxConcurrentProcessing": null,    // images processed at once; null = CPU cores (restart)
    "ThreadsPerImage": 1                // libvips threads per image; 1 is fastest for servers (restart)
  },
  "Defaults": {
    "Quality": 80,
    "ProgressiveJpeg": true,
    "WebpEffort": 4,                    // 0–6: lower is faster, but larger
    "AvifEffort": 0                     // 0–9: first response stays fast; Optimize improves it later
  },
  "Formats": {
    "AutoByDefault": false,             // requests without format= get AVIF/WebP when supported
    "AutoForPlainRequests": false,      // also convert /img/a.jpg without any query
    "Preference": ["avif", "webp"]
  },
  "Optimize": {                         // re-encode WebP/AVIF at higher effort in the background
    "Enabled": true,
    "WebpEffort": 4,
    "AvifEffort": 4,
    "MaxConcurrent": 1,                 // (restart)
    "QueueLimit": 10000,                // (restart)
    "Timeout": "00:02:00",
    "MinSavingsPercent": 1              // only replace files that get at least this much smaller
  },
  "Masters": {                          // faster repeat sizes of large photos, see docs/performance.md
    "Enabled": false,
    "Size": 2560,
    "Quality": 95,
    "MinSourceBytes": 1500000
  },
  "Security": {
    "HmacKey": null                     // set to process signed URLs only
  }
}
```

## How it works

1. A request with commands is normalized: parameters are sorted, clamped and given defaults, and unknown ones are dropped. Equivalent URLs therefore share one cached file.
2. If the cached file exists and the original hasn't changed since, it is served (with `ETag` and `304 Not Modified`).
3. Otherwise the image is processed once, even if many requests for it arrive at the same time, then cached and served.

The cache keeps itself within `MaxAge` and `MaxSizeMB`. Changing an original makes the next request reprocess it.

**Multiple servers:** put a CDN in front, or point `Cache:Path` at shared storage. ETags are identical on every server.

## Extending

Register your own implementation before or after `AddBildverk()`:

| Interface | Default | Replace to |
|---|---|---|
| `IImageSource` | `FileSystemImageSource` | Read originals from blob storage, a database, … |
| `IImageCache` | `FileSystemImageCache` | Cache somewhere other than the local disk |
| `IImageProcessor` | `VipsImageProcessor` | Use a different imaging library |

## Production checklist

- **Restrict variants:** set `Security:HmacKey` or `Limits:AllowedWidths`, so visitors can't create endless size variants. Bildverk warns at startup when neither is set.
- **Size the cache:** set `Cache:MaxSizeMB` to fit your disk.
- **Formats:** remove input formats you don't serve from `Limits:AllowedInputFormats`.
- **More than one server:** put a CDN or shared storage in front.

What Bildverk protects against, and how, is described in [docs/security.md](https://github.com/NodeNestor/bildverk/blob/main/docs/security.md).

## Limits

- Animated GIF and WebP output only the first frame.
- There is no built-in blob-storage cache yet. Use `IImageCache`, a CDN, or a shared disk.

## Development

```sh
dotnet test --project tests/Bildverk.Tests
dotnet run --project samples/Bildverk.Sample --launch-profile http   # http://localhost:5026
```

See [CHANGELOG.md](https://github.com/NodeNestor/bildverk/blob/main/CHANGELOG.md) for changes, and [SECURITY.md](https://github.com/NodeNestor/bildverk/blob/main/SECURITY.md) for reporting vulnerabilities.

## License

MIT. Bildverk uses [NetVips](https://github.com/kleisauke/net-vips) (MIT) and its bundled libvips build (LGPL-3.0, shipped as a separate replaceable library, so it is fine for commercial and closed-source apps). See [docs/security.md](https://github.com/NodeNestor/bildverk/blob/main/docs/security.md#dependencies).
