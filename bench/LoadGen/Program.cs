using System.Diagnostics;
using System.Net;

// Starts each host in its own process on the same images, then measures:
//  A. cold, one request at a time (latency per transform)
//  B. cold burst: many different URLs at once (throughput + peak memory)
//  C. warm: cached URLs under load (requests/sec)
// Usage: LoadGen <benchDir>
var bench = Path.GetFullPath(args.Length > 0 ? args[0] : ".");
var images = Directory.GetFiles(Path.Combine(bench, "images"), "*.jpg").Select(Path.GetFileName).Order().ToArray();

string[] transforms =
[
    "width=1920&height=600&rmode=crop&quality=80",
    "width=300&height=300&rmode=crop",
    "width=800&format=webp&quality=75",
    "width=1200",
];

var hosts = new (string Name, string Dll, int Port)[]
{
    ("Bildverk", Path.Combine(bench, "BildverkHost/bin/Release/net10.0/BildverkHost.dll"), 5401),
};

var results = new List<string>();
var only = args.Length > 1 ? args[1] : null;
foreach (var host in hosts.Where(h => only is null || h.Name == only))
{
    var cache = Path.Combine(bench, "out", host.Name + "-cache");
    if (Directory.Exists(cache)) Directory.Delete(cache, true);
    Directory.CreateDirectory(cache);

    var url = $"http://127.0.0.1:{host.Port}";
    using var proc = Process.Start(new ProcessStartInfo("dotnet", [host.Dll, Path.Combine(bench, "images"), cache, url])
    {
        RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
    })!;
    var log = new StreamWriter(Path.Combine(bench, "out", host.Name + ".log")) { AutoFlush = true };
    proc.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (log) log.WriteLine(e.Data); };
    proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) Console.Error.WriteLine($"[{host.Name}] {e.Data}"); };
    proc.BeginOutputReadLine();
    proc.BeginErrorReadLine();

    using var http = new HttpClient(new SocketsHttpHandler { MaxConnectionsPerServer = 256 }) { BaseAddress = new Uri(url), Timeout = TimeSpan.FromMinutes(5) };
    for (var i = 0; ; i++)
    {
        try { if ((await http.GetAsync($"/{images[0]}")).IsSuccessStatusCode) break; } catch (HttpRequestException) { }
        if (i > 100) throw new Exception($"{host.Name} did not start");
        await Task.Delay(200);
    }

    // Warm up JIT and codecs on URLs that are not measured.
    foreach (var img in images.Take(2)) foreach (var t in transforms) await Get(http, $"/{img}?{t}&width=37");
    var baseMem = Mem(proc);

    Console.WriteLine($"\n=== {host.Name} ===");

    // A. Cold, sequential.
    var cold = new List<(string Img, string T, double Ms, long Bytes, string Type)>();
    foreach (var img in images)
        foreach (var t in transforms)
        {
            var (ms, bytes, type) = await Get(http, $"/{img}?{t}");
            cold.Add((img!, t, ms, bytes, type));
        }
    foreach (var size in new[] { "4mp", "24mp" })
        foreach (var t in transforms)
        {
            var rows = cold.Where(r => r.Img.Contains($"-{size}.") && r.T == t).ToList();
            var line = $"{host.Name,-15} A cold   {size,-4} {t,-46} median {Median(rows.Select(r => r.Ms)),7:0.0} ms   avg size {rows.Average(r => r.Bytes) / 1024,7:0.0} KB  {rows[0].Type}";
            Console.WriteLine(line); results.Add(line);
        }

    // B. Cold burst: 8 images x 12 widths = 96 different URLs, 32 at a time.
    var burst = images.SelectMany(img => Enumerable.Range(0, 12).Select(i => $"/{img}?width={310 + i * 130}")).ToArray();
    long peak = 0;
    using var cts = new CancellationTokenSource();
    var sampler = Task.Run(async () => { while (!cts.IsCancellationRequested) { peak = Math.Max(peak, Mem(proc)); await Task.Delay(25); } });
    var sw = Stopwatch.StartNew();
    await Parallel.ForEachAsync(burst, new ParallelOptions { MaxDegreeOfParallelism = 32 }, async (u, _) => await Get(http, u));
    sw.Stop();
    cts.Cancel(); await sampler;
    var bline = $"{host.Name,-15} B burst  96 uncached URLs @32 parallel: {sw.Elapsed.TotalSeconds,6:0.00} s  = {burst.Length / sw.Elapsed.TotalSeconds,6:0.0} images/s   peak RAM {peak / 1048576,5} MB (idle {baseMem / 1048576} MB)";
    Console.WriteLine(bline); results.Add(bline);

    // D. srcset: 5 sizes per photo, as a responsive <img srcset> page would request them (cold, sequential).
    foreach (var size in new[] { "4mp", "24mp" })
    {
        var srcsetMs = 0.0;
        foreach (var img in images.Where(i => i!.Contains($"-{size}.")))
            foreach (var w in new[] { 1920, 1280, 960, 640, 320 })
                srcsetMs += (await Get(http, $"/{img}?width={w}&quality=80")).Ms;
        var dline = $"{host.Name,-15} D srcset {size,-4} 4 photos x 5 sizes (cold): {srcsetMs,7:0} ms total = {srcsetMs / 20,5:0.0} ms per image";
        Console.WriteLine(dline); results.Add(dline);
    }

    // C. Warm: cached URLs from A, 64 parallel.
    var warmUrls = cold.Select(r => $"/{r.Img}?{r.T}").ToArray();
    var lat = new double[20000];
    sw.Restart();
    await Parallel.ForEachAsync(Enumerable.Range(0, lat.Length), new ParallelOptions { MaxDegreeOfParallelism = 64 }, async (i, _) =>
        lat[i] = (await Get(http, warmUrls[i % warmUrls.Length])).Ms);
    sw.Stop();
    Array.Sort(lat);
    var wline = $"{host.Name,-15} C warm   20000 cached requests @64 parallel: {lat.Length / sw.Elapsed.TotalSeconds,8:0} req/s   p50 {lat[lat.Length / 2],5:0.0} ms  p99 {lat[(int)(lat.Length * 0.99)],5:0.0} ms";
    Console.WriteLine(wline); results.Add(wline);

    // C2. Ceiling: the same response bytes as plain static files (UseStaticFiles), same load.
    var staticDir = Path.Combine(bench, "images", "static-" + host.Name);
    Directory.CreateDirectory(staticDir);
    var staticUrls = new List<string>();
    for (var i = 0; i < warmUrls.Length; i++)
    {
        var bytes = await (await http.GetAsync(warmUrls[i])).Content.ReadAsByteArrayAsync();
        await File.WriteAllBytesAsync(Path.Combine(staticDir, $"{i}.jpg"), bytes);
        staticUrls.Add($"/static-{host.Name}/{i}.jpg");
    }
    sw.Restart();
    await Parallel.ForEachAsync(Enumerable.Range(0, lat.Length), new ParallelOptions { MaxDegreeOfParallelism = 64 }, async (i, _) =>
        lat[i] = (await Get(http, staticUrls[i % staticUrls.Count])).Ms);
    sw.Stop();
    Array.Sort(lat);
    var sline = $"{host.Name,-15} C2 static ceiling, same bytes @64:        {lat.Length / sw.Elapsed.TotalSeconds,8:0} req/s   p50 {lat[lat.Length / 2],5:0.0} ms  p99 {lat[(int)(lat.Length * 0.99)],5:0.0} ms";
    Console.WriteLine(sline); results.Add(sline);
    Directory.Delete(staticDir, true);

    var cacheMb = new DirectoryInfo(cache).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) / 1048576.0;
    var cline = $"{host.Name,-15} cache on disk: {cacheMb:0.0} MB";
    Console.WriteLine(cline); results.Add(cline);

    proc.Kill(entireProcessTree: true);
    await proc.WaitForExitAsync();
}

File.WriteAllLines(Path.Combine(bench, "out", $"results-{DateTime.Now:yyyyMMdd-HHmmss}.txt"), results);

static long Mem(Process p) { p.Refresh(); return p.WorkingSet64; }

static double Median(IEnumerable<double> xs) { var a = xs.Order().ToArray(); return a.Length % 2 == 1 ? a[a.Length / 2] : (a[a.Length / 2 - 1] + a[a.Length / 2]) / 2; }

static async Task<(double Ms, long Bytes, string Type)> Get(HttpClient http, string url)
{
    var sw = Stopwatch.StartNew();
    using var r = await http.GetAsync(url);
    var body = await r.Content.ReadAsByteArrayAsync();
    sw.Stop();
    if (r.StatusCode != HttpStatusCode.OK) throw new Exception($"{url} -> {(int)r.StatusCode}");
    return (sw.Elapsed.TotalMilliseconds, body.Length, r.Content.Headers.ContentType?.MediaType ?? "?");
}
