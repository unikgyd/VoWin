param(
    [Parameter(Mandatory = $true)] [uint32] $InterfaceIndex,
    [Parameter(Mandatory = $true)] [string] $LocalAddress,
    [Parameter(Mandatory = $true)] [string] $PcscfAddress,
    [Parameter(Mandatory = $true)] [string] $NetdumpExecutable
)

# Read-only packet I/O probe: the upstream netdump sample uses WinDivert's
# SNIFF flag. The ICMPv6 echo carries no SIP or subscriber identity.
$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $NetdumpExecutable -PathType Leaf) -or
    [System.IO.Path]::GetFileName($NetdumpExecutable) -ne 'netdump.exe') {
    throw 'An existing official WinDivert netdump.exe is required.'
}
$toolDirectory = [System.IO.Path]::GetDirectoryName([System.IO.Path]::GetFullPath($NetdumpExecutable))
if (-not (Test-Path -LiteralPath (Join-Path $toolDirectory 'WinDivert.dll') -PathType Leaf) -or
    -not (Test-Path -LiteralPath (Join-Path $toolDirectory 'WinDivert64.sys') -PathType Leaf)) {
    throw 'The official WinDivert DLL and signed x64 driver must accompany netdump.exe.'
}
if ((Get-AuthenticodeSignature -LiteralPath (Join-Path $toolDirectory 'WinDivert64.sys')).Status -ne 'Valid') {
    throw 'WinDivert64.sys does not have a valid Authenticode signature.'
}
$localIp = [System.Net.IPAddress]::Parse($LocalAddress)
$remoteIp = [System.Net.IPAddress]::Parse($PcscfAddress)
if ($localIp.AddressFamily -ne [System.Net.Sockets.AddressFamily]::InterNetworkV6 -or
    $remoteIp.AddressFamily -ne [System.Net.Sockets.AddressFamily]::InterNetworkV6) {
    throw 'The packet probe requires IPv6 endpoints.'
}
$filter = "outbound and ipv6 and ifIdx == $InterfaceIndex and icmpv6"
$start = [System.Diagnostics.ProcessStartInfo]::new()
$start.FileName = [System.IO.Path]::GetFullPath($NetdumpExecutable)
$start.ArgumentList.Add($filter)
$start.WorkingDirectory = $toolDirectory
$start.UseShellExecute = $false
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
$start.CreateNoWindow = $true
$start.WindowStyle = [System.Diagnostics.ProcessWindowStyle]::Hidden
$process = [System.Diagnostics.Process]::new()
$process.StartInfo = $start
$started = $false
$stdout = $null
$stderr = $null
$captureText = ''
$errorText = ''
try {
    $started = $process.Start()
    if (-not $started) { throw 'Could not start official netdump.' }
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    Start-Sleep -Seconds 1
    if ($process.HasExited) { throw "netdump failed to open the capture handle: $($stderr.GetAwaiter().GetResult())" }
    # Enough tiny packets to flush the upstream sample's stdio output when
    # redirected to a pipe; only the exact, route-isolated P-CSCF is targeted.
    & "$env:SystemRoot\System32\ping.exe" -6 -n 20 -w 350 -S $($localIp.ToString()) $($remoteIp.ToString()) | Out-Null
    Start-Sleep -Seconds 1
}
finally {
    if ($started -and -not $process.HasExited) {
        $process.Kill()
        $process.WaitForExit(10000) | Out-Null
    }
    if ($started -and $process.HasExited) {
        $captureText = $stdout.GetAwaiter().GetResult()
        $errorText = $stderr.GetAwaiter().GetResult()
    }
    $process.Dispose()
}
if ($errorText) { Write-Output "netdump diagnostic: $errorText" }
$packetCount = 0
foreach ($match in [regex]::Matches($captureText, 'IPv6 \[[^\r\n]*SrcAddr=([^ ]+) DstAddr=([^\]]+)\]')) {
    if ([System.Net.IPAddress]::Parse($match.Groups[1].Value).Equals($localIp) -and
        [System.Net.IPAddress]::Parse($match.Groups[2].Value).Equals($remoteIp)) {
        $packetCount++
    }
}
if ($packetCount -eq 0) {
    throw "Official netdump saw no outbound IPv6 packet on IMS PPP ifIdx $InterfaceIndex. Captured bytes: $($captureText.Length)."
}
Write-Output "Official WinDivert netdump observed $packetCount outbound ICMPv6 packet(s) on IMS PPP ifIdx $InterfaceIndex, source $localIp, destination $remoteIp."
