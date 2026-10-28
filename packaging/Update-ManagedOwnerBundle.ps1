param(
    [Parameter(Mandatory = $true)] [string] $InputFile,
    [Parameter(Mandatory = $true)] [string] $OwnerExecutable,
    [Parameter(Mandatory = $true)] [string] $OutputFile
)

# Build-time incremental packaging only. VoWin never invokes PowerShell at runtime.
# Reuse verified distribution bytes; do not re-export/install drivers or fetch dependencies.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$inputPath = (Resolve-Path -LiteralPath $InputFile).Path
$ownerPath = (Resolve-Path -LiteralPath $OwnerExecutable).Path
$outputPath = [IO.Path]::GetFullPath($OutputFile)
if ($inputPath -ieq $outputPath -or (Test-Path -LiteralPath $outputPath)) {
    throw 'Output must be a new archive; retain the original bundle until verification succeeds.'
}
if ([IO.Path]::GetFileName($ownerPath) -cne 'VoSharp.HostIms.exe') { throw 'Expected the managed single-file owner.' }
$ownerBytes = [IO.File]::ReadAllBytes($ownerPath)
if ($ownerBytes.Length -lt 2 -or $ownerBytes[0] -ne 77 -or $ownerBytes[1] -ne 90) { throw 'Owner must be a Windows executable.' }
function Hash-Bytes([byte[]] $bytes) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash($bytes))).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose() }
}
function Read-Entry($entry) {
    $stream = $entry.Open()
    $memory = [IO.MemoryStream]::new()
    try { $stream.CopyTo($memory); return ,$memory.ToArray() }
    finally { $stream.Dispose(); $memory.Dispose() }
}
$source = [IO.Compression.ZipFile]::OpenRead($inputPath)
try {
    if ($source.Entries.Count -gt 512 -or ($source.Entries | Measure-Object Length -Sum).Sum -gt 64MB) {
        throw 'Unexpected bundle expansion size.'
    }
    $payload = [Collections.Generic.Dictionary[string,byte[]]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $source.Entries) {
        $name = $entry.FullName
        if ($name -match '(^/|\\|:|(^|/)\.\.?(/|$))' -or $name.EndsWith('/')) { throw "Unsafe entry: $name" }
        if ($name -match '(?i)(^powershell/|\.ps1$|(^|/)(RasImsPppProbe\.exe|coreclr\.dll|clrjit\.dll|Microsoft\.WindowsDesktop\.App/))') {
            throw "Forbidden runtime dependency: $name"
        }
        if ($payload.ContainsKey($name)) { throw "Duplicate entry: $name" }
        $payload.Add($name, (Read-Entry $entry))
    }
    if (-not $payload.ContainsKey('manifest.json')) { throw 'Missing manifest.' }
    $manifest = [Text.Encoding]::UTF8.GetString($payload['manifest.json']) | ConvertFrom-Json
    if ($manifest.schemaVersion -ne 1) { throw 'Unsupported manifest.' }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in $manifest.files) {
        if (-not $seen.Add($file.path) -or $file.path -ieq 'manifest.json' -or -not $payload.ContainsKey($file.path)) {
            throw "Invalid manifest entry: $($file.path)"
        }
        $bytes = $payload[$file.path]
        if ($bytes.Length -ne $file.length -or (Hash-Bytes $bytes) -cne $file.sha256) { throw "Checksum mismatch: $($file.path)" }
    }
    if ($seen.Count -ne $payload.Count - 1) { throw 'Archive has unmanifested files.' }
    $ownerEntry = 'host-ims/VoSharp.HostIms.exe'
    if (-not $seen.Contains($ownerEntry)) { throw 'Original managed owner is missing.' }
    $payload[$ownerEntry] = $ownerBytes
    foreach ($file in $manifest.files) {
        if ($file.path -ceq $ownerEntry) {
            $file.length = $ownerBytes.Length
            $file.sha256 = Hash-Bytes $ownerBytes
        }
    }
    $payload['manifest.json'] = [Text.UTF8Encoding]::new($false).GetBytes(($manifest | ConvertTo-Json -Depth 5))
    New-Item -ItemType Directory -Path (Split-Path $outputPath) -Force | Out-Null
    $destination = [IO.Compression.ZipFile]::Open($outputPath, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($name in ($payload.Keys | Sort-Object)) {
            $entry = $destination.CreateEntry($name, [IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime = [DateTimeOffset]::new(2000, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
            $stream = $entry.Open()
            try { $bytes = $payload[$name]; $stream.Write($bytes, 0, $bytes.Length) }
            finally { $stream.Dispose() }
        }
    }
    finally { $destination.Dispose() }
    if ((Get-Item -LiteralPath $outputPath).Length -gt 16MB) { throw 'Bundle exceeds the 16 MiB budget; do not use this output.' }
    Write-Output "Updated only $ownerEntry and manifest; $($seen.Count - 1) dependency files preserved: $outputPath"
}
finally { $source.Dispose() }
