# aspnetcore11-zstandard-compression-demo

ASP.NET Core 11's response compression puts Zstandard (`zstd`) ahead of Brotli and gzip when you leave
the providers at their defaults. This demo serves one JSON response of about 1 MB from a minimal API on
.NET 10 and on .NET 11, asks for it with different `Accept-Encoding` headers, and measures what each
format costs in bytes and in time.

## What it proves

The JSON is one page of 1,500 made-up sales orders, built from a seeded `Random` and whole numbers, so
both runtimes serve the same 1,051,337 bytes (the SHA-256 is printed and compared).

| App | Request | .NET 10.0.15 | .NET 11.0.0 |
|---|---|---|---|
| `AddResponseCompression()` defaults | HTTP, `gzip, deflate, br, zstd` | `br`, 188,178 bytes | `zstd`, 152,923 bytes |
| defaults | HTTP, `gzip, deflate, br` | `br`, 188,178 | `br`, 188,178 |
| defaults | HTTP, `gzip, deflate` | `gzip`, 269,861 | `gzip`, 269,861 |
| defaults | HTTPS, `gzip, deflate, br, zstd` | not compressed | not compressed |
| `EnableForHttps = true` | HTTPS, `gzip, deflate, br, zstd` | `br`, 188,178 | `zstd`, 152,923 |
| defaults + `UseForwardedHeaders` | HTTP with `X-Forwarded-Proto: https` | not compressed | not compressed |
| Brotli listed first (.NET 11) | HTTP, `gzip, deflate, br, zstd` | | `br`, 188,178 |

- The default order changed and nothing else did: Brotli and gzip produce the same bytes on both runtimes.
- Over HTTPS nothing is compressed unless `EnableForHttps` is on. A request that `UseForwardedHeaders`
  turns into HTTPS counts too.
- The three formats don't run at comparable levels by default. gzip and Brotli use
  `CompressionLevel.Fastest` (zlib level 1, Brotli quality 1). Zstandard uses quality 0, which means
  the library's default, level 3. `CompressionLevel.Optimal` maps to that same level 3 for Zstandard.

Sizes of the same payload through the providers, built the way the middleware builds them (.NET 11.0.0):

| Setting | Bytes | Of the original |
|---|---|---|
| gzip, `Fastest` (default) | 269,861 | 25.7 % |
| gzip, `Optimal` (zlib level 6) | 164,376 | 15.6 % |
| Brotli, `Fastest` (default, quality 1) | 188,178 | 17.9 % |
| Brotli, `Optimal` (quality 4) | 148,935 | 14.2 % |
| Zstandard, quality 3 (default, also `Optimal`) | 152,923 | 14.5 % |
| Zstandard, quality 1 (what `Fastest` maps to) | 159,224 | 15.1 % |

Compression time, BenchmarkDotNet 0.15.8 with its default job and the in-process toolchain (warm-up
and pilot runs first, then as many iterations as it needs), run by `check.ps1 -Bench` in the .NET 11
SDK container on an Intel Core Ultra 9 285HX laptop (Docker on Windows 11). Each operation compresses
the whole payload once. The run the post quotes:

```text
BenchmarkDotNet v0.15.8, Linux Ubuntu 26.04.1 LTS (Resolute Raccoon) (container)
Intel Core Ultra 9 285HX 3.07GHz, 1 CPU, 24 logical and 24 physical cores
.NET SDK 11.0.100
  [Host] : .NET 11.0.0 (11.0.0, 11.0.26.53018), X64 RyuJIT x86-64-v3

| Method   | Setting                   | Mean     | Error     | StdDev    | Bytes out |
|--------- |-------------------------- |---------:|----------:|----------:|----------:|
| Compress | br, Fastest (default)     | 1.724 ms | 0.0314 ms | 0.0245 ms |   188,178 |
| Compress | br, Optimal               | 4.305 ms | 0.0812 ms | 0.2289 ms |   148,935 |
| Compress | gzip, Fastest (default)   | 1.578 ms | 0.0272 ms | 0.0212 ms |   269,861 |
| Compress | gzip, Optimal             | 5.970 ms | 0.1170 ms | 0.1094 ms |   164,376 |
| Compress | zstd, quality 1           | 1.296 ms | 0.0221 ms | 0.0196 ms |   159,224 |
| Compress | zstd, quality 3 (default) | 1.480 ms | 0.0288 ms | 0.0456 ms |   152,923 |
```

Two earlier runs on the same machine gave the same order, with means within about 6 % of these
(Brotli at `Optimal` moved the most, 4.09 to 4.53 ms). `check.ps1` asserts the bytes, not the times:
times depend on the machine, so the script only prints them.

## What Edge sends

`edge-accept-encoding.ps1` runs the .NET 11 app in serve mode in a container and opens it with the Edge
installed on the machine, headless, with a profile folder of its own that the script deletes at the end.
Edge 157.0.4341.60 on Windows 11:

| URL | Edge sent | The .NET 11 app answered |
|---|---|---|
| `http://localhost:18680/echo` (defaults) | `gzip, deflate, br, zstd` | `zstd` |
| `https://localhost:18681/echo` (defaults) | `gzip, deflate, br, zstd` | not compressed |
| `https://localhost:18682/echo` (`EnableForHttps`) | `gzip, deflate, br, zstd` | `zstd` |
| `http://<the machine's network address>:18680/echo` | `gzip, deflate` | `gzip` |

So Edge asks for `br` and `zstd` over HTTPS, and over plain HTTP only when the host is `localhost`.
Chrome has supported `zstd` since version 123 ([Chrome Platform Status](https://chromestatus.com/feature/6186023867908096))
and Firefox since 126 ([MDN compatibility data](https://developer.mozilla.org/en-US/docs/Web/HTTP/Reference/Headers/Content-Encoding#browser_compatibility)).
Firefox's defaults (`modules/libpref/init/all.js`) send `gzip, deflate` over HTTP and
`gzip, deflate, br, zstd` over HTTPS.

## How to run it

You need Docker and PowerShell 7. Nothing is installed on the host: everything builds and runs in
Microsoft's SDK images, `mcr.microsoft.com/dotnet/sdk:10.0` and `mcr.microsoft.com/dotnet/sdk:11.0`.

```powershell
pwsh -NoProfile -File check.ps1            # both probes and the sizes, then the assertions
pwsh -NoProfile -File check.ps1 -Edge      # also ask the local Edge what it sends (Windows)
pwsh -NoProfile -File check.ps1 -Bench     # also time the compressors with BenchmarkDotNet
```

`check.ps1` asserts every encoding in the tables above, that every response decodes back to the exact
payload, and that each size matches the number in the post within 1 %. The 1 % is there because a
different build of the native zlib-ng, Brotli or zstd libraries can move a size by a few bytes; on the
images used here they match to the byte. It exits 0 and prints `ALL CHECKS PASSED`. Timing isn't
asserted, because it depends on the machine.

To run one piece by hand:

```powershell
docker run --rm -v "${PWD}:/src" -w /src/src/net11 mcr.microsoft.com/dotnet/sdk:11.0 dotnet run -c Release -- probe
docker run --rm -v "${PWD}:/src" -w /src/src/net10 mcr.microsoft.com/dotnet/sdk:10.0 dotnet run -c Release -- probe
docker run --rm -v "${PWD}:/src" -w /src/src/bench mcr.microsoft.com/dotnet/sdk:11.0 dotnet run -c Release -- bench
```

Or serve it and try it with curl:

```powershell
docker run --rm -p 18680:8080 -p 18681:8443 -p 18682:8444 -v "${PWD}:/src" -w /src/src/net11 mcr.microsoft.com/dotnet/sdk:11.0 dotnet run -c Release -- serve
curl -s -o NUL -D - -H "Accept-Encoding: gzip, deflate, br, zstd" http://localhost:18680/orders
```

## What's in it

- `src/shared/` holds the payload, the apps and the probe. `src/net10/` and `src/net11/` are two project
  files over the same code, so each SDK image builds its own. `src/net11/BrotliFirst.cs` is the app with
  Brotli pinned first; it exists only on .NET 11, where there's a Zstandard provider to put after it.
- The probe starts the apps on loopback ports inside the container, sends 5 warm-up requests and 25
  measured ones per row, and prints the median. Those request times are on loopback and mostly HTTP
  overhead; use the benchmark for the cost of compressing.
- `src/bench/` compresses the payload with `GzipCompressionProvider`, `BrotliCompressionProvider` and
  `ZstandardCompressionProvider` directly, each writing into a `MemoryStream` in one call, as the
  middleware does when the endpoint writes the body in one call. The bytes match the wire.

## Tool versions

- .NET SDK 11.0.100, ASP.NET Core and .NET runtime 11.0.0 (`mcr.microsoft.com/dotnet/sdk:11.0`)
- .NET SDK 10.0.404, ASP.NET Core and .NET runtime 10.0.15 (`mcr.microsoft.com/dotnet/sdk:10.0`)
- BenchmarkDotNet 0.15.8, in-process toolchain
- Docker 29.9.3 on Windows 11, PowerShell 7.7.0
- Microsoft Edge 157.0.4341.60

## Edge cases the post leaves out

**Adding one provider removes the others.** `Providers` is empty by default and the middleware fills in
its own list only when it is. Add just `BrotliCompressionProvider` and you get Brotli and nothing else.
That's why `BrotliFirst.cs` lists all three.

**q-values beat the provider order.** `Accept-Encoding: br;q=1, zstd;q=0.5` gets `br` on .NET 11. The
order only breaks ties between equal q-values.

**.NET 10 ignores `zstd` alone.** A client that sends only `zstd` gets an uncompressed response from
.NET 10, because there's no provider for it.

**Forwarded headers switch compression off.** With `UseForwardedHeaders` reading `X-Forwarded-Proto`
before `UseResponseCompression`, a plain-HTTP request from a TLS-ending proxy becomes `https` and the
middleware skips it, the same as a direct HTTPS request. `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true`
turns that middleware on.

**`IHttpsCompressionFeature`** can switch compression on or off per request, instead of for the whole
app with `EnableForHttps`. The demo doesn't use it.

**IIS and Azure App Service** can compress at the web server even when the app doesn't, so the
`Content-Encoding` you see there may not come from ASP.NET Core
([response compression docs](https://learn.microsoft.com/aspnet/core/performance/response-compression)).

**Small responses.** The payload here is about 1 MB. The middleware has no minimum size and compresses
a 30-byte response too; the trade-off on tiny bodies isn't measured here.

**Request decompression.** The same pull request added a Zstandard provider to the request
decompression middleware. Not covered.

## License

MIT
