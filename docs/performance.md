# Performance

All numbers were measured on 2026-10-15 on a 24-thread Windows desktop with .NET 10 and Bildverk's default settings. The tools that produced them are in `bench/`, so you can run them on your own images.

## Compared with ImageSharp.Web

The same 8 high-detail photos (four 4 MP, four 24 MP) were served over loopback HTTP, with both libraries on default settings. ImageSharp.Web 3.2.0 was used, because 3.3.0 and later refuse to build without a Six Labors licence key.

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
| Peak memory during the burst | 723 MB | **520 MB** | ~2 060 MB |
| Cached requests, 64 parallel | ~14 100 req/s | ~15 100 req/s | ~11 400 req/s |

- **Defaults favour file size.** They are progressive JPEG and WebP effort 4, so JPEG from small originals is about as fast as ImageSharp but produces smaller files.
- **Cached requests** run as fast as plain `UseStaticFiles` serving the same bytes.

To run it: put JPEGs in `bench/images/`, build `bench/BildverkHost` and `bench/ImageSharpHost` with `-c Release`, then run

```sh
dotnet bench/LoadGen/bin/Release/net10.0/LoadGen.dll bench [Bildverk|ImageSharp.Web]
```

## File size at the same visual quality

Comparing files at the same `quality=` number is misleading, because every encoder looks different at a given value. `bench/Codecs` instead measures how many bytes each encoder needs to reach the same visual quality: SSIM against the uncompressed image, with the target set to what JPEG reaches at quality 80. The test set was the 24 [Kodak](https://r0k.us/graphics/kodak/) photos at 768 and 400 px.

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

- **Effort matters on normal photos.** Low efforts are much worse on smooth areas such as sky or product backgrounds. On very detailed textures the difference almost disappears, so measure your own images before tuning.
- **AVIF is the smallest.** After background optimization it needs about 44 % fewer bytes than JPEG and about 16 % fewer than WebP.

To run it: `CODEC_WIDTHS=768,400 dotnet bench/Codecs/bin/Release/net10.0/Codecs.dll <folder> <images…>`.

## Where the time goes

- **Decoding the original JPEG is the floor.** libvips decodes at 1/2, 1/4 or 1/8 size when the output is small, but every byte of the file is still read once. That is about 40 ms for an 8 MB, 24 MP photo. `bench/Profile` shows the time per stage for any image.
- **The encoder dominates WebP and AVIF.** That is why AVIF is encoded fast first and improved in the background (`Optimize`).
- **Bildverk itself adds about 5–10 ms per new image:** HTTP, cache write and header checks.
- **Throughput:** each image uses one libvips thread, and as many images as there are CPU cores run in parallel. Letting libvips also spread each image over all cores oversubscribed the CPU and cut burst throughput by 2.4×.

## Master copies

With `Masters:Enabled`, the first resize of a large original also stores a 2560 px copy at JPEG quality 95. "Large" means the long side is at least 1.5 × `Size`. Later sizes are made from that copy instead of decoding the full original again.

- **When it helps:** large photos used in many sizes, such as a srcset, or a gallery with thumbnails. Later sizes are about 2× faster.
- **Cost:**
  - The first request for each large photo takes about 2× as long.
  - Each large original needs one extra cache file (~3–4 MB).
  - There is a small quality loss from the extra JPEG generation: 38–46 dB PSNR compared with resizing directly, measured on worst-case textures.
- **Details:** sizes computed from the aspect ratio can differ by 1 px because of rounding. Requests larger than the master always use the original.

## Scaling

- **One server:** processing uses all cores, and a cache hit is sent straight from disk.
- **Several servers:** put a CDN in front, or share `Cache:Path` between them. ETags are identical on every server.
- **Bursts:** `MaxConcurrentProcessing` queues work instead of running out of memory. Many requests for the same new image are processed once.
