using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains.InProcess.Emit;
using CompressionDemo;
using Microsoft.AspNetCore.ResponseCompression;

// sizes: compress the payload once per setting and print the bytes (fast, the same on every run).
// bench: time each setting with BenchmarkDotNet, the bytes shown in their own column.
var mode = args.Length > 0 ? args[0] : "sizes";

if (mode == "sizes")
{
    var jsonOut = args.SkipWhile(a => a != "--json").Skip(1).FirstOrDefault();
    Console.WriteLine($"runtime: {RuntimeInformation.FrameworkDescription}");
    Console.WriteLine($"payload: {Payload.Json.Length:N0} bytes, sha256 {Payload.Sha256}");
    Console.WriteLine();
    Console.WriteLine($"{"setting",-28} {"bytes",9} {"of original",12}");
    var rows = new List<object>();
    foreach (var setting in Settings.All())
    {
        var bytes = Sizes.Of(setting);
        Console.WriteLine($"{setting.Name,-28} {bytes,9:N0} {(double)bytes / Payload.Json.Length,12:P1}");
        rows.Add(new { setting = setting.Name, setting.Encoding, bytes });
    }
    if (jsonOut is not null)
    {
        File.WriteAllText(jsonOut, JsonSerializer.Serialize(
            new { runtime = RuntimeInformation.FrameworkDescription, payloadBytes = Payload.Json.Length, payloadSha256 = Payload.Sha256, rows },
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
    }
    return 0;
}

if (mode == "bench")
{
    var config = DefaultConfig.Instance
        .AddJob(Job.Default.WithToolchain(InProcessEmitToolchain.Instance))
        .AddColumn(new SizeColumn())
        .WithArtifactsPath(args.SkipWhile(a => a != "--artifacts").Skip(1).FirstOrDefault() ?? "BenchmarkDotNet.Artifacts")
        .WithSummaryStyle(SummaryStyle.Default.WithMaxParameterColumnWidth(40))
        .WithOptions(ConfigOptions.JoinSummary);
    var summary = BenchmarkRunner.Run<CompressPayload>(config);

    // The same results, short enough to read on a phone: mean time and bytes.
    Console.WriteLine();
    Console.WriteLine($"{"setting",-27} {"mean",8} {"bytes",9}");
    foreach (var setting in Settings.All())
    {
        var report = summary.Reports.First(r => ((Setting)r.BenchmarkCase.Parameters["Setting"]).Name == setting.Name);
        var meanMs = report.ResultStatistics!.Mean / 1_000_000;
        Console.WriteLine($"{setting.Name,-27} {meanMs,5:F2} ms {Sizes.Of(setting),9:N0}");
    }
    return 0;
}

Console.Error.WriteLine("usage: CompressionBench [sizes [--json <file>] | bench [--artifacts <folder>]]");
return 2;

// One provider at one level, built the way the middleware builds it.
public sealed record Setting(string Name, string Encoding, ICompressionProvider Provider)
{
    public override string ToString() => Name;
}

public static class Settings
{
    public static IEnumerable<Setting> All() =>
    [
        new("gzip, Fastest (default)", "gzip", new GzipCompressionProvider(new GzipCompressionProviderOptions())),
        new("gzip, Optimal", "gzip", new GzipCompressionProvider(new GzipCompressionProviderOptions { Level = CompressionLevel.Optimal })),
        new("br, Fastest (default)", "br", new BrotliCompressionProvider(new BrotliCompressionProviderOptions())),
        new("br, Optimal", "br", new BrotliCompressionProvider(new BrotliCompressionProviderOptions { Level = CompressionLevel.Optimal })),
        new("zstd, quality 3 (default)", "zstd", new ZstandardCompressionProvider(new ZstandardCompressionProviderOptions())),
        new("zstd, quality 1", "zstd", new ZstandardCompressionProvider(new ZstandardCompressionProviderOptions
        {
            CompressionOptions = new ZstandardCompressionOptions { Quality = 1 },
        })),
    ];
}

public static class Sizes
{
    // Writes the whole payload through the provider's stream, as the middleware
    // does when the endpoint writes it in one call, and counts the output.
    public static long Of(Setting setting)
    {
        using var output = new MemoryStream();
        using (var stream = setting.Provider.CreateStream(output))
        {
            stream.Write(Payload.Json);
        }
        return output.Length;
    }
}

public class CompressPayload
{
    public static IEnumerable<Setting> AllSettings => Settings.All();

    [ParamsSource(nameof(AllSettings))]
    public Setting Setting { get; set; } = null!;

    readonly MemoryStream output = new(capacity: 2 * 1024 * 1024);

    [Benchmark]
    public long Compress()
    {
        output.SetLength(0);
        using (var stream = Setting.Provider.CreateStream(output))
        {
            stream.Write(Payload.Json);
        }
        return output.Length;
    }
}

public sealed class SizeColumn : IColumn
{
    public string Id => nameof(SizeColumn);
    public string ColumnName => "Bytes out";
    public bool AlwaysShow => true;
    public ColumnCategory Category => ColumnCategory.Custom;
    public int PriorityInCategory => 0;
    public bool IsNumeric => true;
    public UnitType UnitType => UnitType.Dimensionless;
    public string Legend => "Compressed size of the payload";
    public bool IsAvailable(Summary summary) => true;
    public bool IsDefault(Summary summary, BenchmarkCase benchmarkCase) => false;
    public string GetValue(Summary summary, BenchmarkCase benchmarkCase) => GetValue(summary, benchmarkCase, SummaryStyle.Default);
    public string GetValue(Summary summary, BenchmarkCase benchmarkCase, SummaryStyle style) =>
        Sizes.Of((Setting)benchmarkCase.Parameters["Setting"]).ToString("N0");
}
