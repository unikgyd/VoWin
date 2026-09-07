# download-native.ps1
# Downloads Wintun prebuilt signed binaries into native/amd64/
# strongSwan (charon-svc.exe) must be built separately by the user.
#
# Usage: powershell -ExecutionPolicy Bypass -File download-native.ps1

$ErrorActionPreference = "Stop"
$NativeDir = "$PSScriptRoot\native\amd64"

Write-Host "=== VoSharp Native Binary Downloader ===" -ForegroundColor Cyan
Write-Host "Target directory: $NativeDir"

if (!(Test-Path $NativeDir)) {
    New-Item -ItemType Directory -Path $NativeDir -Force | Out-Null
}

$tempDir = Join-Path ([System.IO.Path]::GetTempPath()) "voSharp-native-dl"
if (!(Test-Path $tempDir)) { New-Item -ItemType Directory -Path $tempDir -Force | Out-Null }

# ─── Wintun 0.14.1 ───────────────────────────────────────────────────────────

$wintunUrl  = "https://www.wintun.net/builds/wintun-0.14.1.zip"
$wintunZip  = Join-Path $tempDir "wintun-0.14.1.zip"
$wintunHash = "07c256185d6ee3652e09fa55c0b673e2624b565e02c4b9091c79ca7d2f24ef51"
$wintunDll  = Join-Path $NativeDir "wintun.dll"

if (Test-Path $wintunDll) {
    Write-Host "[SKIP] wintun.dll already exists" -ForegroundColor Yellow
} else {
    Write-Host "[DOWNLOAD] Wintun 0.14.1..." -ForegroundColor Green
    Invoke-WebRequest -Uri $wintunUrl -OutFile $wintunZip -UseBasicParsing

    $actualHash = (Get-FileHash $wintunZip -Algorithm SHA256).Hash
    if ($actualHash -ne $wintunHash.ToUpper()) {
        throw "Wintun SHA256 mismatch! Expected: $wintunHash, Got: $actualHash"
    }

    Expand-Archive -Path $wintunZip -DestinationPath $tempDir -Force
    $src = "$tempDir\wintun\bin\amd64\wintun.dll"
    if (!(Test-Path $src)) {
        $src = Get-ChildItem -Path $tempDir -Recurse -Filter "wintun.dll" |
               Where-Object { $_.FullName -match "amd64" } | Select-Object -First 1 -ExpandProperty FullName
    }
    Copy-Item $src $wintunDll -Force
    Write-Host "[OK] wintun.dll -> $wintunDll" -ForegroundColor Green
}

# ─── strongSwan ──────────────────────────────────────────────────────────────

$charonExe = Join-Path $NativeDir "charon-svc.exe"
if (!(Test-Path $charonExe)) {
    Write-Host ""
    Write-Host "[NOTE] charon-svc.exe is NOT included in this download." -ForegroundColor Magenta
    Write-Host "       You must build strongSwan for Windows using MSYS2/MinGW-W64." -ForegroundColor Magenta
    Write-Host "       Then place charon-svc.exe and its DLLs into: $NativeDir" -ForegroundColor Magenta
    Write-Host ""
    Write-Host "       Build instructions: https://wiki.strongswan.org/projects/strongswan/wiki/Windows"
} else {
    Write-Host "[OK] charon-svc.exe found" -ForegroundColor Green
}

# Copy strongswan.conf template
$confSrc = "$PSScriptRoot\native\strongswan.conf"
$confDst = Join-Path $NativeDir "strongswan.conf"
if ((Test-Path $confSrc) -and !(Test-Path $confDst)) {
    Copy-Item $confSrc $confDst -Force
    Write-Host "[OK] strongswan.conf -> $confDst" -ForegroundColor Green
}

# ─── Cleanup ─────────────────────────────────────────────────────────────────

Remove-Item $tempDir -Recurse -Force -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "=== Done ===" -ForegroundColor Cyan
Write-Host "native/amd64/ contents:"
Get-ChildItem $NativeDir | ForEach-Object {
    Write-Host ("  {0,-30} {1,12:N0} bytes" -f $_.Name, $_.Length)
}
