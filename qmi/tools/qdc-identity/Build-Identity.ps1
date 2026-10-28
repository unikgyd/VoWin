param(
    # Link-only copies of the target firmware's libc.so.6 and libdl.so.2.
    [Parameter(Mandatory = $true)] [string] $SysrootDirectory,
    [string] $Clang = 'clang',
    [string] $OutputFile = (Join-Path $PSScriptRoot 'vowin-qdc-identity.armv7')
)
$ErrorActionPreference = 'Stop'
$qdcSysroot = (Resolve-Path -LiteralPath $SysrootDirectory).Path
foreach ($qdcLibrary in @('libc.so.6', 'libdl.so.2')) {
    if (-not (Test-Path -LiteralPath (Join-Path $qdcSysroot $qdcLibrary))) { throw "Missing target link library: $qdcLibrary" }
}
& $Clang --target=armv7-linux-gnueabi -mfloat-abi=soft -Oz -Wall -Wextra -Werror `
    -fno-stack-protector -fno-pie -no-pie -fuse-ld=lld -nostdlib `
    (Join-Path $PSScriptRoot 'start.S') (Join-Path $PSScriptRoot 'identity.c') "-L$qdcSysroot" `
    '-Wl,--dynamic-linker=/lib/ld-linux.so.3' '-Wl,-z,noexecstack' '-Wl,-z,relro' '-Wl,-z,now' `
    '-Wl,--build-id=sha1' '-Wl,--strip-all' '-l:libc.so.6' '-l:libdl.so.2' -o $OutputFile
if ($LASTEXITCODE -ne 0) { throw 'QDC507 identity helper build failed.' }
$qdcBuiltFile = Get-Item -LiteralPath $OutputFile
[ordered]@{
    path = $qdcBuiltFile.FullName
    bytes = $qdcBuiltFile.Length
    sha256 = (Get-FileHash -LiteralPath $OutputFile -Algorithm SHA256).Hash.ToLowerInvariant()
} | ConvertTo-Json
