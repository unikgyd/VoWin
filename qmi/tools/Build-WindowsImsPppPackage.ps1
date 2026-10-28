param([Parameter(Mandatory = $true)] [string] $OutputDirectory)

$ErrorActionPreference = 'Stop'
$package = Join-Path ([System.IO.Path]::GetFullPath($OutputDirectory)) 'host-ims'
$ownerProject = Join-Path $PSScriptRoot '../../voSharp/src/VoSharp.HostIms/VoSharp.HostIms.csproj'
New-Item -ItemType Directory -Path $package -Force | Out-Null
& dotnet publish $ownerProject -c Release -r win-x64 --self-contained false -o $package
if ($LASTEXITCODE -ne 0) { throw 'Managed IMS owner publish failed.' }
$symbols = Join-Path $package 'VoSharp.HostIms.pdb'
if (Test-Path -LiteralPath $symbols) { Remove-Item -LiteralPath $symbols -Force }
Write-Output "Packaged C# Windows IMS / RAS owner: $package"
