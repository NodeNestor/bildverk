using System.Collections.Concurrent;
using System.Diagnostics;
using Bildverk;
using Bildverk.Commands;
using Bildverk.Processing;
using NetVips;

// Mutation fuzzer for the processing path: corrupted images x random commands through VipsImageProcessor,
// exactly as the middleware calls it. Expected outcomes: a result, or ImageProcessingException (4xx).
// Anything else is a finding: another exception type, a hang past the time limit, or a native crash
// (the process dies; the input is in out/fuzz/current.* so it can be replayed).
// Usage: Fuzz <seedImagesDir | -> <outDir> [seconds=180] [workers=12]     ("-" = generated seed images, used in CI)
var seedDir = args[0];
var outDir = Directory.CreateDirectory(args[1]).FullName;
var seconds = args.Length > 2 ? int.Parse(args[2]) : 180;
var workers = args.Length > 3 ? int.Parse(args[3]) : 12;

NetVips.NetVips.BlockUntrusted = true;
NetVips.NetVips.Concurrency = 1;
var processor = new VipsImageProcessor();
var limits = new LimitsOptions { MaxSourcePixels = 50_000_000, MaxSourceBytes = 50L * 1024 * 1024 };

// Seeds: every allowed input format, small enough to keep iterations fast.
var seeds = new List<(string Name, byte[] Data)>();
foreach (var (name, src) in SeedImages(seedDir))
{
    using (src)
    {
        foreach (var ext in new[] { ".jpg", ".png", ".webp", ".gif", ".avif", ".tif" })
            seeds.Add(($"{name}{ext}", src.WriteToBuffer(ext)));
        seeds.Add(($"{name}-progressive.jpg", src.WriteToBuffer(".jpg[interlace=true]")));
        using var alpha = src.Bandjoin(128);
        seeds.Add(($"{name}-alpha.png", alpha.WriteToBuffer(".png")));
    }
}
Console.WriteLine($"{seeds.Count} seeds, {workers} workers, {seconds} s");

long runs = 0, ok = 0, rejected = 0;
var findings = new ConcurrentBag<string>();
var slowest = 0.0;
var deadline = DateTime.UtcNow.AddSeconds(seconds);

await Parallel.ForEachAsync(Enumerable.Range(0, workers), new ParallelOptions { MaxDegreeOfParallelism = workers }, async (worker, _) =>
{
    var rng = new Random(worker * 7919 + Environment.TickCount);
    while (DateTime.UtcNow < deadline)
    {
        var seed = seeds[rng.Next(seeds.Count)];
        var data = Mutate(seed.Data, rng, seeds);
        var commands = RandomCommands(rng);
        var format = (OutputFormat)rng.Next(6);
        var request = new ProcessingRequest(commands, format, rng.Next(1, 101), limits, new EncoderSettings(rng.Next(2) == 0, rng.Next(0, 3), 0), TimeSpan.FromSeconds(10));

        // Keep the current input on disk so a native crash can be reproduced.
        var current = Path.Combine(outDir, $"current-{worker}.bin");
        await File.WriteAllBytesAsync(current, data);
        await File.WriteAllTextAsync(current + ".txt", $"{seed.Name} {CommandParser.Canonicalize(commands)} -> {format}");

        var sw = Stopwatch.StartNew();
        try
        {
            var result = await processor.ProcessAsync(new MemoryStream(data), request, CancellationToken.None);
            if (result.Data.Length == 0) Record("empty output");
            Interlocked.Increment(ref ok);
        }
        catch (ImageProcessingException)
        {
            Interlocked.Increment(ref rejected);
        }
        catch (Exception ex)
        {
            Record($"{ex.GetType().Name}: {ex.Message.Split('\n')[0]}");
        }

        var ms = sw.Elapsed.TotalMilliseconds;
        if (ms > 12_000) Record($"took {ms:0} ms (time limit is 10 s)");
        InterlockedMax(ref slowest, ms);
        var n = Interlocked.Increment(ref runs);
        if (n % 2000 == 0) Console.WriteLine($"  {n} runs, {ok} ok, {rejected} rejected, {findings.Count} findings, slowest {slowest:0} ms");

        void Record(string what)
        {
            var id = $"finding-{findings.Count:D3}";
            File.Copy(current, Path.Combine(outDir, id + ".bin"), true);
            File.WriteAllText(Path.Combine(outDir, id + ".txt"), $"{what}\n{File.ReadAllText(current + ".txt")}");
            findings.Add(what);
        }
    }
});

Console.WriteLine($"\nDONE: {runs} runs, {ok} processed, {rejected} rejected (4xx), slowest {slowest:0} ms");
Console.WriteLine(findings.IsEmpty ? "No findings." : $"{findings.Count} FINDINGS:\n  " + string.Join("\n  ", findings.GroupBy(f => f).Select(g => $"{g.Count()}x {g.Key}")));
return findings.IsEmpty ? 0 : 1;

static byte[] Mutate(byte[] input, Random rng, List<(string Name, byte[] Data)> seeds)
{
    var d = (byte[])input.Clone();
    var rounds = rng.Next(1, 6);
    for (var r = 0; r < rounds; r++)
    {
        switch (rng.Next(8))
        {
            case 0: // bit flips
                for (var i = rng.Next(1, 16); i > 0; i--) d[rng.Next(d.Length)] ^= (byte)(1 << rng.Next(8));
                break;
            case 1: // interesting bytes
                byte[] magic = [0x00, 0xFF, 0x7F, 0x80, 0x01, 0xFE];
                for (var i = rng.Next(1, 8); i > 0; i--) d[rng.Next(d.Length)] = magic[rng.Next(magic.Length)];
                break;
            case 2: // header stomp: big integers early in the file, where dimensions and lengths live
                if (d.Length < 8) break;
                var at = rng.Next(Math.Min(d.Length - 4, 512));
                BitConverter.GetBytes(rng.Next(4) switch { 0 => int.MaxValue, 1 => 0x7FFF_0000, 2 => 65535, _ => rng.Next() }).CopyTo(d, at);
                break;
            case 3: // truncate
                d = d[..rng.Next(1, d.Length)];
                break;
            case 4: // duplicate a chunk
                var a = rng.Next(d.Length); var len = rng.Next(1, Math.Min(4096, d.Length - a + 1));
                d = [.. d[..a], .. d[a..(a + len)], .. d[a..]];
                break;
            case 5: // splice with another seed
                var other = seeds[rng.Next(seeds.Count)].Data;
                var cut = rng.Next(Math.Min(d.Length, other.Length));
                d = [.. d[..cut], .. other[cut..]];
                break;
            case 6: // delete a chunk
                var s = rng.Next(d.Length); var e = Math.Min(d.Length, s + rng.Next(1, 256));
                d = [.. d[..s], .. d[e..]];
                break;
            default: // random tail garbage
                var tail = new byte[rng.Next(1, 2048)]; rng.NextBytes(tail);
                d = [.. d, .. tail];
                break;
        }
        if (d.Length == 0) d = [0];
    }
    return d;
}

// 96 px seeds: real photos from a folder, or generated ones (gradient, noise, flat) when the folder is "-".
static IEnumerable<(string Name, Image Image)> SeedImages(string dir)
{
    if (dir != "-")
    {
        foreach (var file in Directory.GetFiles(dir).Order().Take(4))
            yield return (Path.GetFileNameWithoutExtension(file), Image.ThumbnailBuffer(File.ReadAllBytes(file), 96).CopyMemory());
        yield break;
    }

    using var xyz = Image.Xyz(96, 64);
    using var x = xyz[0];
    using var y = xyz[1];
    yield return ("gradient", Image.Arrayjoin([x * 2.6, y * 3.9, (x + y) * 1.5]).Cast(Enums.BandFormat.Uchar).CopyMemory());
    yield return ("noise", Image.Gaussnoise(96, 64, mean: 128, sigma: 60).Bandjoin([Image.Gaussnoise(96, 64), Image.Gaussnoise(96, 64)]).Cast(Enums.BandFormat.Uchar).CopyMemory());
    yield return ("flat", Image.Black(96, 64, bands: 3).NewFromImage(30, 120, 200).CopyMemory());
}

static ImageCommands RandomCommands(Random rng) => new()
{
    Width = rng.Next(4) == 0 ? null : rng.Next(1, 600),
    Height = rng.Next(4) == 0 ? null : rng.Next(1, 600),
    Mode = (ResizeMode)rng.Next(6),
    Anchor = (Anchor)rng.Next(9),
    Focal = rng.Next(4) == 0 ? (rng.NextDouble(), rng.NextDouble()) : null,
    Background = rng.Next(3) == 0 ? new Rgba((byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256)) : null,
};

static void InterlockedMax(ref double target, double value)
{
    double current;
    while (value > (current = Volatile.Read(ref target)) && Interlocked.CompareExchange(ref target, value, current) != current) { }
}
