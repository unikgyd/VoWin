param(
    [string] $OutputFile = (Join-Path $PSScriptRoot 'runtime.zip'),
    # Optional OEM INF/CAT/SYS distribution; never auto-export the user's driver store.
    [string] $ModemDriverDirectory,
    # Optional already-installed WinDivert distribution; exact pinned bytes and signature still required.
    [string] $WinDivertDirectory
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot
$cache = Join-Path $PSScriptRoot 'cache'
$lock = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'dependencies.json') -Raw | ConvertFrom-Json
New-Item -ItemType Directory -Path $cache -Force | Out-Null
$stage = Join-Path $cache ('stage-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage | Out-Null

function Get-Archive($dependency) {
    $file = Join-Path $cache ([uri]$dependency.url).Segments[-1]
    if (-not (Test-Path -LiteralPath $file) -or
        (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ine $dependency.sha256) {
        Invoke-WebRequest -Uri $dependency.url -OutFile $file
    }
    if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ine $dependency.sha256) {
        throw "Redistributable checksum mismatch: $($dependency.url)"
    }
    return $file
}

function Assert-Signed($file) {
    if ((Get-AuthenticodeSignature -LiteralPath $file).Status -ne 'Valid') {
        throw "Refusing to package an unsigned or invalid executable/driver: $file"
    }
}

try {
    # IMS / RAS / recovery are C# and use the installed .NET runtime.
    & (Join-Path $repo 'qmi/tools/Build-WindowsImsPppPackage.ps1') -OutputDirectory $stage
    $hostIms = Join-Path $stage 'host-ims'
    if ($WinDivertDirectory) {
        $divertSource = (Resolve-Path -LiteralPath $WinDivertDirectory).Path
        foreach ($name in @('WinDivert.dll', 'WinDivert64.sys', 'WinDivert.LICENSE.txt')) {
            $source = Join-Path $divertSource $name
            if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ine $lock.winDivert.x64Files.$name) {
                throw "Installed WinDivert component checksum mismatch: $name"
            }
            Copy-Item -LiteralPath $source -Destination $hostIms
        }
    }
    else {
        $divert = Join-Path $stage '_divert'
        Expand-Archive -LiteralPath (Get-Archive $lock.winDivert) -DestinationPath $divert
        $divertRoot = Join-Path $divert 'WinDivert-2.2.2-A'
        foreach ($name in @('WinDivert.dll', 'WinDivert64.sys')) {
            Copy-Item -LiteralPath (Join-Path $divertRoot "x64/$name") -Destination $hostIms
        }
        Copy-Item -LiteralPath (Join-Path $divertRoot 'LICENSE') -Destination (Join-Path $hostIms 'WinDivert.LICENSE.txt')
        Remove-Item -LiteralPath $divert -Recurse -Force
    }
    foreach ($name in @('WinDivert.dll', 'WinDivert64.sys', 'WinDivert.LICENSE.txt')) {
        if ((Get-FileHash -LiteralPath (Join-Path $hostIms $name) -Algorithm SHA256).Hash -ine $lock.winDivert.x64Files.$name) {
            throw "Packaged WinDivert component checksum mismatch: $name"
        }
    }
    Assert-Signed (Join-Path $hostIms 'WinDivert64.sys')
    # WinDivert's LGPL license permits replacement of the extracted DLL/driver;
    # an explicit override remains available in advanced diagnostics.

    $adbStage = Join-Path $stage '_adb'
    Expand-Archive -LiteralPath (Get-Archive $lock.platformTools) -DestinationPath $adbStage
    $adb = Join-Path $stage 'adb'
    New-Item -ItemType Directory -Path $adb | Out-Null
    foreach ($name in @('adb.exe', 'AdbWinApi.dll', 'AdbWinUsbApi.dll', 'NOTICE.txt', 'source.properties')) {
        Copy-Item -LiteralPath (Join-Path $adbStage "platform-tools/$name") -Destination $adb
    }
    Remove-Item -LiteralPath $adbStage -Recurse -Force

    $voice = Join-Path $stage 'voice'
    New-Item -ItemType Directory -Path $voice | Out-Null
    $voiceBase = "https://raw.githubusercontent.com/moluncn/mavo/$($lock.voiceCommit)"
    foreach ($file in $lock.voiceFiles) {
        $cached = Join-Path $cache $file.name
        if (-not (Test-Path -LiteralPath $cached) -or
            (Get-FileHash -LiteralPath $cached).Hash -ine $file.sha256) {
            Invoke-WebRequest "$voiceBase/Resources/ModuleVoice/$($file.name)" -OutFile $cached
        }
        if ((Get-FileHash -LiteralPath $cached).Hash -ine $file.sha256) { throw "Voice checksum mismatch: $($file.name)" }
        Copy-Item -LiteralPath $cached -Destination $voice
    }
    foreach ($name in @('COPYING-GPL-2.0', 'MODULE-REPORT.md')) {
        Invoke-WebRequest "$voiceBase/Resources/ModuleVoice/$name" -OutFile (Join-Path $voice $name)
    }
    Invoke-WebRequest "$voiceBase/LICENSE" -OutFile (Join-Path $voice 'MaVo.LICENSE.txt')
    Invoke-WebRequest "$voiceBase/THIRD_PARTY_NOTICES.md" -OutFile (Join-Path $voice 'MaVo.THIRD_PARTY_NOTICES.md')

    if ($ModemDriverDirectory) {
        $driverSource = (Resolve-Path -LiteralPath $ModemDriverDirectory).Path
        $infs = @(Get-ChildItem -LiteralPath $driverSource -Recurse -Filter '*.inf' -File)
        $cats = @(Get-ChildItem -LiteralPath $driverSource -Recurse -Filter '*.cat' -File)
        if ($infs.Count -eq 0 -or $cats.Count -eq 0) { throw 'OEM driver distribution requires INF and signed CAT files.' }
        foreach ($cat in $cats) { Assert-Signed $cat.FullName }
        foreach ($inf in $infs) {
            if ((Get-Content -LiteralPath $inf.FullName -Raw) -notmatch '(?i)Quectel') {
                throw "Only the explicitly selected Quectel driver package may be included: $($inf.Name)"
            }
        }
        Copy-Item -LiteralPath $driverSource -Destination (Join-Path $stage 'drivers') -Recurse
    }

    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'THIRD-PARTY-NOTICES.txt') -Destination $stage
    Copy-Item -LiteralPath (Join-Path $repo 'LICENSE') -Destination (Join-Path $stage 'VoWin.LICENSE.txt')
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'OpenCORE-AMR.LICENSE.txt') -Destination $stage
    $files = @(Get-ChildItem -LiteralPath $stage -Recurse -File | Sort-Object FullName | ForEach-Object {
        [ordered]@{
            path = [IO.Path]::GetRelativePath($stage, $_.FullName).Replace('\', '/')
            length = $_.Length
            sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    })
    if (@($files | Where-Object { $_.path -match '(?i)(^powershell/|\.ps1$|(^|/)(RasImsPppProbe\.exe|coreclr\.dll|clrjit\.dll|Microsoft\.WindowsDesktop\.App/))' }).Count) {
        throw 'Runtime bundle must not contain PowerShell, the removed native RAS helper or a private .NET runtime.'
    }
    [ordered]@{ schemaVersion = 1; files = $files } | ConvertTo-Json -Depth 5 |
        Set-Content -LiteralPath (Join-Path $stage 'manifest.json') -Encoding utf8NoBOM
    $output = [IO.Path]::GetFullPath($OutputFile)
    New-Item -ItemType Directory -Path (Split-Path $output) -Force | Out-Null
    # Only overwrite the explicitly named generated artifact.
    $temporaryArchive = "$output.$([guid]::NewGuid().ToString('N')).tmp"
    $zip = [IO.Compression.ZipFile]::Open($temporaryArchive, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in @(Get-ChildItem -LiteralPath $stage -Recurse -File | Sort-Object FullName)) {
            $relative = [IO.Path]::GetRelativePath($stage, $file.FullName).Replace('\', '/')
            $entry = $zip.CreateEntry($relative, [IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime = [DateTimeOffset]::new(2000, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
            $input = [IO.File]::OpenRead($file.FullName)
            $entryOutput = $entry.Open()
            try { $input.CopyTo($entryOutput) }
            finally { $entryOutput.Dispose(); $input.Dispose() }
        }
    }
    finally { $zip.Dispose() }
    if ((Get-Item -LiteralPath $temporaryArchive).Length -gt 16MB) { throw 'Communication bundle exceeded the 16 MiB size budget.' }
    Move-Item -LiteralPath $temporaryArchive -Destination $output -Force
    Write-Output "Runtime bundle ready: $output ($($files.Count) verified files; OEM drivers: $([bool]$ModemDriverDirectory))"
}
finally {
    # The staging path is freshly generated below this script's cache, never user input.
    if ([IO.Path]::GetFullPath($stage).StartsWith([IO.Path]::GetFullPath($cache) + [IO.Path]::DirectorySeparatorChar) -and
        (Test-Path -LiteralPath $stage)) { Remove-Item -LiteralPath $stage -Recurse -Force }
}
