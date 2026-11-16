# Asks the Edge installed on this machine what it sends in Accept-Encoding, over
# HTTP and over HTTPS, and which encoding the .NET 11 app picked for it.
# Starts the app in serve mode in the SDK container (ports 18680-18682), runs
# Edge headless with a profile folder of its own, and removes that folder and
# stops the container at the end, also on failure and on Ctrl+C.
param(
  [string]$Edge = 'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe',
  [string]$NuGetCache = (Join-Path $env:USERPROFILE '.nuget\packages-linux')
)
$ErrorActionPreference = 'Stop'
$demo = $PSScriptRoot
$container = 'p68-serve'
$work = Join-Path ([IO.Path]::GetTempPath()) ('p68-edge-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
$profileDir = Join-Path $work 'profile'
New-Item -ItemType Directory -Force $profileDir | Out-Null
New-Item -ItemType Directory -Force $NuGetCache | Out-Null

try {
  "edge: $((Get-Item $Edge).VersionInfo.ProductVersion)"
  docker run -d --rm --name $container -p 18680:8080 -p 18681:8443 -p 18682:8444 `
    -v "${demo}:/src" -v "${NuGetCache}:/root/.nuget/packages" -w /src/src/net11 `
    mcr.microsoft.com/dotnet/sdk:11.0 dotnet run -c Release -- serve | Out-Null

  $deadline = (Get-Date).AddMinutes(3)
  while ($true) {
    try { Invoke-WebRequest http://localhost:18680/echo -TimeoutSec 2 | Out-Null; break }
    catch { if ((Get-Date) -gt $deadline) { throw 'the app did not start' }; Start-Sleep -Seconds 2 }
  }

  # localhost counts as a secure context in Chromium, so also try the machine's
  # own network address over plain HTTP, which does not.
  $lan = (Get-NetIPAddress -AddressFamily IPv4 |
    Where-Object { $_.IPAddress -notlike '127.*' -and $_.IPAddress -notlike '169.254.*' -and $_.InterfaceAlias -notmatch 'vEthernet|Loopback|WSL' } |
    Select-Object -First 1).IPAddress
  $urls = @('http://localhost:18680/echo', 'https://localhost:18681/echo', 'https://localhost:18682/echo')
  if ($lan) { $urls += "http://${lan}:18680/echo" }
  foreach ($url in $urls) {
    "edge opens $url"
    # --dump-dom makes Edge exit once the page has loaded.
    $edgeArgs = @('--headless=new', '--disable-gpu', "--user-data-dir=$profileDir", '--ignore-certificate-errors',
                  '--dump-dom', $url)
    Start-Process -FilePath $Edge -ArgumentList $edgeArgs -Wait -WindowStyle Hidden `
      -RedirectStandardOutput (Join-Path $work 'stdout.txt') -RedirectStandardError (Join-Path $work 'stderr.txt')
  }

  Start-Sleep -Seconds 1
  docker logs $container 2>&1 | Select-String '^ECHO Edg/' | ForEach-Object { $_.Line.Substring(5) }
}
finally {
  docker stop -t 3 $container 2>$null | Out-Null
  # Edge keeps its files locked for a moment after it exits: retry, then stop
  # any Edge process still using this profile, then try again.
  foreach ($attempt in 1..10) {
    if (-not (Test-Path $work)) { break }
    try { [IO.Directory]::Delete($work, $true) } catch { Start-Sleep -Milliseconds 700 }
  }
  if (Test-Path $work) {
    Get-CimInstance Win32_Process -Filter "Name = 'msedge.exe'" |
      Where-Object { $_.CommandLine -like "*$profileDir*" } |
      ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Seconds 1
    try { [IO.Directory]::Delete($work, $true) } catch { }
  }
  "edge profile left behind: $(Test-Path $work)"
}
