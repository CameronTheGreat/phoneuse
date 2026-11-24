# Is a saved UxPlay bundle (package.ps1 -UxPlayBundle) still current? It lists, in packages.txt, the
# MSYS2 packages its files came from at the versions it was made with; this compares them with the
# versions in MSYS2's package index today (ucrt64.db, ~600 KB, only listed: its entries are named
# <package>-<version>-<release>/). Any difference, or no index, and the bundle is deleted so
# package.ps1 makes it again from an up-to-date MSYS2. (The scripts that make it are CI's cache key.)
#
#   powershell -ExecutionPolicy Bypass -File windows\scripts\uxplay-bundle-check.ps1 <bundle folder>
# In GitHub Actions also sets the step output "current" (true/false).
param([Parameter(Mandatory)][string]$Bundle)
$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

function Set-Result([bool]$current, [string]$why) {
    Write-Host "==> $why"
    if (-not $current -and (Test-Path $Bundle)) { Remove-Item -Recurse -Force $Bundle }
    if ($env:GITHUB_OUTPUT) { "current=$($current.ToString().ToLower())" | Out-File -Append -Encoding ascii $env:GITHUB_OUTPUT }
    exit 0
}

$made = Join-Path $Bundle "packages.txt"
if (-not (Test-Path (Join-Path $Bundle "complete")) -or -not (Test-Path $made)) { Set-Result $false "no saved UxPlay bundle: making one" }
$madeWith = Get-Content $made | ForEach-Object { $_.Trim() } | Where-Object { $_ }

try {
    $db = Join-Path ([IO.Path]::GetTempPath()) "phoneuse-ucrt64.db"
    $tries = 0
    while ($true) {
        try { Invoke-WebRequest "https://repo.msys2.org/mingw/ucrt64/ucrt64.db" -OutFile $db -UseBasicParsing; break }
        catch { if (++$tries -ge 3) { throw }; Start-Sleep 5 }
    }
    # Windows' own bsdtar reads zstd; Git's GNU tar, often first on PATH, doesn't by itself
    $entries = & "$env:SystemRoot\System32\tar.exe" -tf $db
    if ($LASTEXITCODE -ne 0) { throw "couldn't list it" }
} catch {
    if ($env:GITHUB_ACTIONS) { Write-Host "::warning::couldn't read MSYS2's package index ($_): making the UxPlay bundle again" }
    Set-Result $false "couldn't read MSYS2's package index ($_): making the bundle again"
}
$today = @{}
foreach ($e in $entries) { if ($e -match '^([^/]+)/$') { $today[$Matches[1]] = $true } }

$moved = @($madeWith | Where-Object { -not $today.ContainsKey($_) })
if ($moved.Count) {
    Set-Result $false "MSYS2 has moved on from $($moved.Count) of the bundle's $($madeWith.Count) packages ($(($moved | Select-Object -First 5) -join ', ')$(if ($moved.Count -gt 5) { ', ...' })): making it again"
}
Set-Result $true "the saved UxPlay bundle is current: all $($madeWith.Count) of its packages are MSYS2's latest"
