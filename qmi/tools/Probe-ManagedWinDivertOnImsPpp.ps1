param(
    [Parameter(Mandatory = $true)] [uint32] $InterfaceIndex,
    [Parameter(Mandatory = $true)] [string] $LocalAddress,
    [Parameter(Mandatory = $true)] [string] $PcscfAddress,
    [Parameter(Mandatory = $true)] [string] $WinDivertDll,
    [string] $TestProject = (Join-Path $PSScriptRoot '..\..\voSharp\tests\VoSharp.Tests\VoSharp.Tests.csproj')
)

# One deliberately invalid ESP packet, with synthetic SPI/payload and no SIP
# identity, tests the raw Windows PPP egress. Pktmon provides an independent
# in-box observation; it is never started if another capture/filter exists.
$ErrorActionPreference = 'Stop'
$pktmon = Join-Path $env:SystemRoot 'System32\pktmon.exe'
foreach ($path in @($WinDivertDll, $TestProject, $pktmon)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing probe dependency: $path" }
}
$status = (& $pktmon status) -join "`n"
if ($LASTEXITCODE -ne 0 -or $status -notmatch '没有运行|not running') {
    throw 'Pktmon is already running or its state is unknown; refusing to disturb another capture.'
}
$filters = (& $pktmon filter list) -join "`n"
if ($LASTEXITCODE -ne 0 -or $filters -notmatch '(?m)^\s*(无|None)\s*$') {
    throw 'Pktmon already has packet filters; refusing to replace them.'
}
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'The real IMS PPP packet probe requires an elevated Windows token.'
}
$local = [System.Net.IPAddress]::Parse($LocalAddress)
$pcscf = [System.Net.IPAddress]::Parse($PcscfAddress)
if ($local.AddressFamily -ne [System.Net.Sockets.AddressFamily]::InterNetworkV6 -or
    $pcscf.AddressFamily -ne [System.Net.Sockets.AddressFamily]::InterNetworkV6) {
    throw 'Real IMS PPP ESP probe requires IPv6 endpoints.'
}
$id = [guid]::NewGuid().ToString('N')
$filterName = "VoWinEsp-$id"
$etl = Join-Path $env:TEMP "VoWinEsp-$id.etl"
$pcap = Join-Path $env:TEMP "VoWinEsp-$id.pcapng"
$filterAdded = $false
$captureStarted = $false
$priorDll = $env:VOWIN_REAL_PPP_ESP_PROBE_DLL
$priorLocal = $env:VOWIN_REAL_PPP_ESP_LOCAL
$priorPcscf = $env:VOWIN_REAL_PPP_ESP_PCSCF
$priorIndex = $env:VOWIN_REAL_PPP_ESP_INDEX
try {
    & $pktmon filter add $filterName -t 50 -i $($local.ToString()) $($pcscf.ToString()) | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Pktmon rejected the exact IPv6 ESP filter.' }
    $filterAdded = $true
    & $pktmon start --capture --comp all --pkt-size 0 --file-name $etl | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Pktmon could not start the bounded ESP capture.' }
    $captureStarted = $true
    $env:VOWIN_REAL_PPP_ESP_PROBE_DLL = [System.IO.Path]::GetFullPath($WinDivertDll)
    $env:VOWIN_REAL_PPP_ESP_LOCAL = $local.ToString()
    $env:VOWIN_REAL_PPP_ESP_PCSCF = $pcscf.ToString()
    $env:VOWIN_REAL_PPP_ESP_INDEX = [string]$InterfaceIndex
    & dotnet test $TestProject --no-build --no-restore --verbosity quiet `
        --logger 'console;verbosity=detailed' `
        --filter 'FullyQualifiedName~WindowsHostImsWinDivertRealPppTests'
    $testExit = $LASTEXITCODE
    if ($testExit -ne 0) { throw "Managed WinDivert real PPP send failed ($testExit)." }
}
finally {
    $env:VOWIN_REAL_PPP_ESP_PROBE_DLL = $priorDll
    $env:VOWIN_REAL_PPP_ESP_LOCAL = $priorLocal
    $env:VOWIN_REAL_PPP_ESP_PCSCF = $priorPcscf
    $env:VOWIN_REAL_PPP_ESP_INDEX = $priorIndex
    if ($captureStarted) {
        & $pktmon stop | Out-Null
        if ($LASTEXITCODE -ne 0) { Write-Warning 'Pktmon stop failed; inspect its state.' }
    }
    if ($filterAdded) {
        & $pktmon filter remove | Out-Null
        if ($LASTEXITCODE -ne 0) { Write-Warning 'Pktmon filter cleanup failed.' }
    }
}
& $pktmon etl2pcap $etl --out $pcap | Out-Null
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $pcap -PathType Leaf)) {
    throw "Pktmon could not convert the ESP capture: $etl"
}
$bytes = [System.IO.File]::ReadAllBytes($pcap)
$marker = [byte[]]@(0xA1,0xB2,0xC3,0xD4,0,0,0,1,
    [byte][char]'V',[byte][char]'o',[byte][char]'W',[byte][char]'i',
    [byte][char]'n',[byte][char]'P',[byte][char]'P',[byte][char]'P')
$found = $false
for ($i = 0; $i -le $bytes.Length - $marker.Length; $i++) {
    if ($bytes[$i] -ne $marker[0]) { continue }
    $matches = $true
    for ($j = 1; $j -lt $marker.Length; $j++) {
        if ($bytes[$i + $j] -ne $marker[$j]) { $matches = $false; break }
    }
    if ($matches) { $found = $true; break }
}
if (-not $found) {
    throw "Managed WinDivert accepted one ESP packet, but Pktmon did not prove PPP egress (pcapng bytes=$($bytes.Length); capture=$pcap)."
}
Write-Output "Windows Pktmon independently captured the synthetic ESP marker after C# WinDivertSend on IMS PPP ifIdx $InterfaceIndex."
Remove-Item -LiteralPath $etl,$pcap -Force
