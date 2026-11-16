using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace CompressionDemo;

// Starts the apps on loopback ports, requests /orders with different
// Accept-Encoding headers, and prints what came back: the encoding the
// middleware chose, the bytes on the wire, and the median request time.
public static class Probe
{
    const int WarmUp = 5;
    const int Measured = 25;

    public sealed record Row(
        string Scenario, string Scheme, string AcceptEncoding, string? ForwardedProto,
        string ContentEncoding, int Bytes, double MedianMs, bool DecodesToPayload);

    public static async Task<int> RunAsync(string[] args)
    {
        var jsonOut = args.SkipWhile(a => a != "--json").Skip(1).FirstOrDefault();
        _ = Payload.Json.Length; // build the payload before any server starts
        var cert = Apps.SelfSignedLocalhost();

        var apps = new List<(string Name, WebApplication App)>
        {
            ("defaults", Apps.Defaults(k => { k.ListenLocalhost(5101); k.ListenLocalhost(5443, l => l.UseHttps(cert)); })),
            ("https-on", Apps.HttpsOn(k => k.ListenLocalhost(5444, l => l.UseHttps(cert)))),
            ("behind-proxy", Apps.BehindProxy(k => k.ListenLocalhost(5102))),
#if NET11_0_OR_GREATER
            ("brotli-first", Apps.BrotliFirst(k => k.ListenLocalhost(5103))),
#endif
        };
        foreach (var (_, app) in apps) await app.StartAsync();

        var all = "gzip, deflate, br, zstd";
        var cases = new List<(string Scenario, string Url, string AcceptEncoding, string? ForwardedProto)>
        {
            ("defaults", "http://localhost:5101/orders", all, null),
            ("defaults", "http://localhost:5101/orders", "gzip, deflate, br", null),
            ("defaults", "http://localhost:5101/orders", "gzip, deflate", null),
            ("defaults", "http://localhost:5101/orders", "zstd", null),
            ("defaults", "http://localhost:5101/orders", "br", null),
            ("defaults", "http://localhost:5101/orders", "gzip", null),
            ("defaults", "http://localhost:5101/orders", "identity", null),
            ("defaults", "http://localhost:5101/orders", "br;q=1, zstd;q=0.5", null),
            ("defaults", "https://localhost:5443/orders", all, null),
            ("https-on", "https://localhost:5444/orders", all, null),
            ("behind-proxy", "http://localhost:5102/orders", all, null),
            ("behind-proxy", "http://localhost:5102/orders", all, "https"),
#if NET11_0_OR_GREATER
            ("brotli-first", "http://localhost:5103/orders", all, null),
            ("brotli-first", "http://localhost:5103/orders", "gzip, deflate, zstd", null),
#endif
        };

        using var http = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.None,
            SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true },
        });

        var rows = new List<Row>();
        foreach (var c in cases)
        {
            rows.Add(await MeasureAsync(http, c.Scenario, c.Url, c.AcceptEncoding, c.ForwardedProto));
        }

        foreach (var (_, app) in apps) await app.StopAsync();

        var runtime = RuntimeInformation.FrameworkDescription;
        Console.WriteLine($"runtime: {runtime}, ASP.NET Core {AspNetCoreVersion}");
        Console.WriteLine($"payload: {Payload.Json.Length:N0} bytes of JSON, {Payload.OrderCount:N0} orders, sha256 {Payload.Sha256}");
        Console.WriteLine($"timing: median of {Measured} requests after {WarmUp} warm-up requests, loopback, HTTP/1.1");
        Console.WriteLine();
        Console.WriteLine($"{"app",-13} {"scheme",-6} {"Accept-Encoding",-26} {"X-Fwd-Proto",-11} {"Content-Encoding",-16} {"bytes",10} {"ms",7}  decodes");
        foreach (var r in rows)
        {
            Console.WriteLine($"{r.Scenario,-13} {r.Scheme,-6} {r.AcceptEncoding,-26} {r.ForwardedProto ?? "-",-11} {r.ContentEncoding,-16} {r.Bytes,10:N0} {r.MedianMs,7:F2}  {(r.DecodesToPayload ? "yes" : "NO")}");
        }

        if (jsonOut is not null)
        {
            var doc = new
            {
                runtime,
                aspnetcore = AspNetCoreVersion,
                payloadBytes = Payload.Json.Length,
                payloadSha256 = Payload.Sha256,
                rows,
            };
            await File.WriteAllTextAsync(jsonOut, JsonSerializer.Serialize(doc, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        }

        return rows.All(r => r.DecodesToPayload) ? 0 : 1;
    }

    // The product version of the ASP.NET Core shared framework, without the commit hash.
    static string AspNetCoreVersion =>
        (typeof(WebApplication).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?").Split('+')[0];

    static async Task<Row> MeasureAsync(HttpClient http, string scenario, string url, string acceptEncoding, string? forwardedProto)
    {
        byte[] body = [];
        string encoding = "";
        var times = new List<double>();

        for (var i = 0; i < WarmUp + Measured; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("Accept-Encoding", acceptEncoding);
            if (forwardedProto is not null) request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", forwardedProto);

            var watch = Stopwatch.StartNew();
            using var response = await http.SendAsync(request);
            body = await response.Content.ReadAsByteArrayAsync();
            watch.Stop();

            response.EnsureSuccessStatusCode();
            encoding = string.Join(",", response.Content.Headers.ContentEncoding);
            if (i >= WarmUp) times.Add(watch.Elapsed.TotalMilliseconds);
        }

        times.Sort();
        var median = times[times.Count / 2];
        return new Row(scenario, new Uri(url).Scheme, acceptEncoding, forwardedProto,
            encoding.Length == 0 ? "none" : encoding, body.Length, median, Decode(encoding, body).AsSpan().SequenceEqual(Payload.Json));
    }

    static byte[] Decode(string encoding, byte[] body)
    {
        if (encoding.Length == 0) return body;
        using var input = new MemoryStream(body);
        using Stream decoder = encoding switch
        {
            "gzip" => new GZipStream(input, CompressionMode.Decompress),
            "br" => new BrotliStream(input, CompressionMode.Decompress),
#if NET11_0_OR_GREATER
            "zstd" => new ZstandardStream(input, CompressionMode.Decompress),
#endif
            _ => throw new NotSupportedException(encoding),
        };
        using var output = new MemoryStream();
        decoder.CopyTo(output);
        return output.ToArray();
    }
}
