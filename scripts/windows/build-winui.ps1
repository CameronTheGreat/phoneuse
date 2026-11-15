# Build the native mirror window (WinUI 3) into winui\dist, where `phoneuse up` finds it.
#   powershell -ExecutionPolicy Bypass -File scripts\windows\build-winui.ps1
# Needs the .NET SDK (10 or later). Without winui\dist\PhoneUseMirror.exe, phoneuse
# falls back to the browser mirror in an Edge/Chrome app window.
$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$Proj = Join-Path $Root "winui\PhoneUseMirror\PhoneUseMirror.csproj"
$Dist = Join-Path $Root "winui\dist"
$arch = if ($env:PROCESSOR_ARCHITECTURE -eq "ARM64") { "ARM64" } else { "x64" }

Get-Process PhoneUseMirror -ErrorAction SilentlyContinue | Stop-Process  # the exe is locked while it runs
# The trailing slash on -o keeps the XAML .xbf files inside dist (without it they land next to it).
dotnet build $Proj -c Release -p:Platform=$arch -o "$Dist/" -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "the mirror window didn't build" }
Write-Host "==> Built $Dist\PhoneUseMirror.exe"
