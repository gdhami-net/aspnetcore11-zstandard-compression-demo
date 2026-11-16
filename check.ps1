# Builds and runs the demo in Microsoft's .NET SDK containers and asserts every
# encoding and size the post states. Exits 0 and ends with ALL CHECKS PASSED
# when they all hold.
#
#   pwsh -NoProfile -File check.ps1            # probes on .NET 10 and .NET 11, plus the sizes
#   pwsh -NoProfile -File check.ps1 -Edge      # also ask the local Edge what it sends (Windows)
#   pwsh -NoProfile -File check.ps1 -Bench     # also time the compressors (several minutes)
param(
  [string]$NuGetCache = (Join-Path $env:USERPROFILE '.nuget\packages-linux'),
  [switch]$Edge,
  [switch]$Bench
)
$ErrorActionPreference = 'Stop'
$demo = $PSScriptRoot
$art = Join-Path $demo 'artifacts'
New-Item -ItemType Directory -Force $art, $NuGetCache | Out-Null

# The numbers the post quotes. A different build of the native zlib-ng, Brotli
# or zstd libraries could move them by a few bytes, so sizes may differ by 1 %.
$PayloadBytes = 1051337
$Tolerance = 0.01
$Expected = [ordered]@{
  'gzip, Fastest (default)'   = 269861
  'gzip, Optimal'             = 164376
  'br, Fastest (default)'     = 188178
  'br, Optimal'               = 148935
  'zstd, quality 3 (default)' = 152923
  'zstd, quality 1'           = 159224
}

$script:failures = 0
function Check([string]$name, [bool]$ok, [string]$detail = '') {
  if ($ok) { "PASS  $name" } else { "FAIL  $name"; $script:failures++ }
  if ($detail) { "      $detail" }
}
function Near([long]$actual, [long]$expected) { [math]::Abs($actual - $expected) -le $expected * $Tolerance }

function Invoke-Sdk([string]$tag, [string]$name, [string]$dir, [string[]]$command) {
  "running: $name (mcr.microsoft.com/dotnet/sdk:$tag)"
  $log = docker run --rm --name $name -v "${demo}:/src" -v "${NuGetCache}:/root/.nuget/packages" -w $dir `
    "mcr.microsoft.com/dotnet/sdk:$tag" @command 2>&1
  if ($LASTEXITCODE -ne 0) { $log | Select-Object -Last 30; throw "$name failed (exit $LASTEXITCODE)" }
  $log | Set-Content (Join-Path $art "$name.log")
}

Invoke-Sdk '10.0' 'p68-check-net10' '/src/src/net10' @('dotnet', 'run', '-c', 'Release', '--', 'probe', '--json', '/src/artifacts/probe-net10.json')
Invoke-Sdk '11.0' 'p68-check-net11' '/src/src/net11' @('dotnet', 'run', '-c', 'Release', '--', 'probe', '--json', '/src/artifacts/probe-net11.json')
Invoke-Sdk '11.0' 'p68-check-sizes' '/src/src/bench' @('dotnet', 'run', '-c', 'Release', '--', 'sizes', '--json', '/src/artifacts/sizes-net11.json')
''

$p10 = Get-Content (Join-Path $art 'probe-net10.json') -Raw | ConvertFrom-Json
$p11 = Get-Content (Join-Path $art 'probe-net11.json') -Raw | ConvertFrom-Json
$sizes = Get-Content (Join-Path $art 'sizes-net11.json') -Raw | ConvertFrom-Json

function Row($probe, [string]$app, [string]$scheme, [string]$accept, [string]$fwd = '') {
  # forwardedProto is null in the JSON when no X-Forwarded-Proto was sent.
  $probe.rows | Where-Object {
    $_.scenario -eq $app -and $_.scheme -eq $scheme -and $_.acceptEncoding -eq $accept -and "$($_.forwardedProto)" -eq $fwd
  } | Select-Object -First 1
}
function Show($row) { if ($row) { "Content-Encoding $($row.contentEncoding), $('{0:N0}' -f $row.bytes) bytes" } else { 'no such row' } }

$all = 'gzip, deflate, br, zstd'

Check 'the .NET 10 probe ran on .NET 10' ($p10.runtime -like '.NET 10.*') $p10.runtime
Check 'the .NET 11 probe ran on the released .NET 11 (no preview or rc)' ($p11.runtime -like '.NET 11.*' -and $p11.runtime -notmatch 'preview|rc') "$($p11.runtime), ASP.NET Core $($p11.aspnetcore)"
Check "the payload is $('{0:N0}' -f $PayloadBytes) bytes on both runtimes, same SHA-256" (
  $p10.payloadBytes -eq $PayloadBytes -and $p11.payloadBytes -eq $PayloadBytes -and $p10.payloadSha256 -eq $p11.payloadSha256) $p11.payloadSha256

$r = Row $p10 'defaults' 'http' $all
Check ".NET 10, defaults, '$all' -> br" ($r.contentEncoding -eq 'br') (Show $r)
$r = Row $p11 'defaults' 'http' $all
Check ".NET 11, defaults, '$all' -> zstd" ($r.contentEncoding -eq 'zstd') (Show $r)
$r = Row $p11 'defaults' 'http' 'gzip, deflate, br'
Check ".NET 11, defaults, a client without zstd still gets br" ($r.contentEncoding -eq 'br') (Show $r)
foreach ($p in $p10, $p11) {
  $v = if ($p -eq $p10) { '.NET 10' } else { '.NET 11' }
  $r = Row $p 'defaults' 'http' 'gzip, deflate'
  Check "$v, defaults, 'gzip, deflate' (a browser on plain HTTP) -> gzip" ($r.contentEncoding -eq 'gzip') (Show $r)
  $r = Row $p 'defaults' 'https' $all
  Check "$v, defaults, over HTTPS -> not compressed (EnableForHttps is false)" ($r.contentEncoding -eq 'none' -and $r.bytes -eq $PayloadBytes) (Show $r)
  $r = Row $p 'behind-proxy' 'http' $all 'https'
  Check "$v, UseForwardedHeaders + X-Forwarded-Proto: https -> not compressed" ($r.contentEncoding -eq 'none' -and $r.bytes -eq $PayloadBytes) (Show $r)
}
$r = Row $p10 'defaults' 'http' 'zstd'
Check ".NET 10 has no zstd provider: 'zstd' alone -> not compressed" ($r.contentEncoding -eq 'none') (Show $r)
$r = Row $p10 'https-on' 'https' $all
Check ".NET 10, EnableForHttps = true, over HTTPS -> br" ($r.contentEncoding -eq 'br') (Show $r)
$r = Row $p11 'https-on' 'https' $all
Check ".NET 11, EnableForHttps = true, over HTTPS -> zstd" ($r.contentEncoding -eq 'zstd') (Show $r)
$r = Row $p11 'behind-proxy' 'http' $all
Check ".NET 11, plain HTTP from a proxy without forwarded headers -> zstd" ($r.contentEncoding -eq 'zstd') (Show $r)
$r = Row $p11 'defaults' 'http' 'br;q=1, zstd;q=0.5'
Check ".NET 11, q-values win over the provider order: 'br;q=1, zstd;q=0.5' -> br" ($r.contentEncoding -eq 'br') (Show $r)
$r = Row $p11 'brotli-first' 'http' $all
Check ".NET 11, providers listed Brotli first -> br" ($r.contentEncoding -eq 'br') (Show $r)
$r = Row $p11 'brotli-first' 'http' 'gzip, deflate, zstd'
Check ".NET 11, Brotli first, a client without br still gets zstd" ($r.contentEncoding -eq 'zstd') (Show $r)
Check 'every response decodes back to the exact payload' (
  @($p10.rows + $p11.rows | Where-Object { -not $_.decodesToPayload }).Count -eq 0) "$(@($p10.rows).Count + @($p11.rows).Count) responses"

''
foreach ($name in $Expected.Keys) {
  $row = $sizes.rows | Where-Object { $_.setting -eq $name }
  Check "$name is $('{0:N0}' -f $Expected[$name]) bytes (within 1 %)" ($row -and (Near $row.bytes $Expected[$name])) "measured $('{0:N0}' -f $row.bytes)"
}
$size = @{}; foreach ($row in $sizes.rows) { $size[$row.setting] = [long]$row.bytes }
foreach ($pair in @(@('zstd', 'zstd, quality 3 (default)'), @('br', 'br, Fastest (default)'), @('gzip', 'gzip, Fastest (default)'))) {
  $wire = Row $p11 'defaults' 'http' $pair[0]
  Check "the $($pair[0]) response on the wire is the same size as '$($pair[1])'" ($wire.bytes -eq $size[$pair[1]]) "$('{0:N0}' -f $wire.bytes) on the wire"
}
Check 'at the defaults: zstd < br < gzip' ($size['zstd, quality 3 (default)'] -lt $size['br, Fastest (default)'] -and $size['br, Fastest (default)'] -lt $size['gzip, Fastest (default)'])
Check 'Brotli at Optimal is a little smaller than zstd at its default' ($size['br, Optimal'] -lt $size['zstd, quality 3 (default)'])
Check 'zstd at quality 1 is still smaller than Brotli at its default' ($size['zstd, quality 1'] -lt $size['br, Fastest (default)'])

if ($Edge) {
  ''
  $edgeOut = & (Join-Path $demo 'edge-accept-encoding.ps1') -NuGetCache $NuGetCache
  $edgeOut
  $lines = @($edgeOut | Where-Object { $_ -like 'Edg/*' })
  Check 'Edge over HTTPS sends zstd' (@($lines | Where-Object { $_ -match 'https://\S+\s+sent "gzip, deflate, br, zstd"' }).Count -eq 2)
  Check 'Edge, defaults over HTTPS -> not compressed' (@($lines | Where-Object { $_ -match 'https://localhost:18681\s+sent "[^"]*"\s+got none' }).Count -eq 1)
  Check 'Edge, EnableForHttps over HTTPS -> zstd' (@($lines | Where-Object { $_ -match 'https://localhost:18682\s+sent "[^"]*"\s+got zstd' }).Count -eq 1)
  Check 'Edge over plain HTTP to localhost sends zstd and gets it' (
    @($lines | Where-Object { $_ -match 'http://localhost:18680\s+sent "gzip, deflate, br, zstd"\s+got zstd' }).Count -eq 1)
  Check 'Edge over plain HTTP to a network address sends only gzip, deflate' (
    @($lines | Where-Object { $_ -match 'http://(?!localhost)\S+\s+sent "gzip, deflate"\s+got gzip' }).Count -eq 1)
  Check 'Edge left no profile folder behind' (@($edgeOut | Where-Object { $_ -eq 'edge profile left behind: False' }).Count -eq 1)
}

if ($Bench) {
  ''
  Invoke-Sdk '11.0' 'p68-check-bench' '/src/src/bench' @('dotnet', 'run', '-c', 'Release', '--', 'bench', '--artifacts', '/src/artifacts/bench')
  $benchLog = Get-Content (Join-Path $art 'p68-check-bench.log')
  $benchLog | Select-String '^\|' | ForEach-Object { $_.Line }
  ''
  $at = ($benchLog | Select-String -SimpleMatch -CaseSensitive 'setting ' | Select-Object -Last 1).LineNumber
  if ($at) { $benchLog | Select-Object -Skip ($at - 1) -First 7 }
}

''
if ($script:failures -eq 0) { 'ALL CHECKS PASSED'; exit 0 } else { "$($script:failures) CHECK(S) FAILED"; exit 1 }
