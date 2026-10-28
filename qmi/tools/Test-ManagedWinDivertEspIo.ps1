param(
    [Parameter(Mandatory = $true)] [string] $PeerExecutable,
    [Parameter(Mandatory = $true)] [string] $WintunDll,
    [Parameter(Mandatory = $true)] [string] $WinDivertDll,
    [string] $TestProject = (Join-Path $PSScriptRoot '..\..\voSharp\tests\VoSharp.Tests\VoSharp.Tests.csproj')
)

$ErrorActionPreference = 'Stop'
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'The isolated managed WinDivert packet lab requires an elevated Windows token.'
}
foreach ($path in @($PeerExecutable, $WintunDll, $WinDivertDll, $TestProject)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing lab dependency: $path" }
}
if (Get-NetAdapter -Name 'VoWin IMS Lab' -ErrorAction SilentlyContinue) {
    throw 'An adapter with the isolated lab name already exists.'
}
$start = [System.Diagnostics.ProcessStartInfo]::new()
$start.FileName = [System.IO.Path]::GetFullPath($PeerExecutable)
$start.ArgumentList.Add('--managed-windivert-peer')
$start.ArgumentList.Add([System.IO.Path]::GetFullPath($WintunDll))
$start.UseShellExecute = $false
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
$start.CreateNoWindow = $true
$start.WindowStyle = [System.Diagnostics.ProcessWindowStyle]::Hidden
$peer = [System.Diagnostics.Process]::new()
$peer.StartInfo = $start
$priorDll = $env:VOWIN_WINDIVERT_LAB_DLL
$peerStarted = $false
try {
    $peerStarted = $peer.Start()
    if (-not $peerStarted) { throw 'Could not start isolated Wintun peer.' }
    $outputTask = $peer.StandardOutput.ReadToEndAsync()
    $errorTask = $peer.StandardError.ReadToEndAsync()
    $ready = $false
    $deadline = [DateTime]::UtcNow.AddSeconds(12)
    do {
        $adapter = @(Get-NetAdapter -Name 'VoWin IMS Lab' -ErrorAction SilentlyContinue)
        $address = @(Get-NetIPAddress -AddressFamily IPv6 -IPAddress 'fd7a:6f57:494d:5301::1' -ErrorAction SilentlyContinue)
        $route = @(Get-NetRoute -AddressFamily IPv6 -DestinationPrefix 'fd7a:6f57:494d:5301::2/128' -ErrorAction SilentlyContinue)
        if ($adapter.Count -eq 1 -and $adapter[0].Status -eq 'Up' -and
            $address.Count -eq 1 -and $address[0].AddressState -eq 'Preferred' -and
            $route.Count -eq 1 -and $route[0].InterfaceIndex -eq $adapter[0].InterfaceIndex) {
            $ready = $true
            break
        }
        if ($peer.HasExited) { throw "Wintun peer exited before readiness: $($peer.ExitCode)" }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $deadline)
    if (-not $ready) { throw 'Isolated Wintun address/route did not become ready.' }
    $env:VOWIN_WINDIVERT_LAB_DLL = [System.IO.Path]::GetFullPath($WinDivertDll)
    & dotnet test $TestProject --no-build --no-restore --verbosity quiet `
        --logger 'console;verbosity=detailed' `
        --filter 'FullyQualifiedName~WindowsHostImsWinDivertIoLiveTests'
    $testExit = $LASTEXITCODE
    if (-not $peer.WaitForExit(40000)) { throw 'Wintun managed peer did not finish within 40 seconds.' }
    $peerOutput = $outputTask.GetAwaiter().GetResult()
    $peerError = $errorTask.GetAwaiter().GetResult()
    Write-Output $peerOutput
    if ($peerError) { Write-Output "Wintun peer stderr: $peerError" }
    if ($testExit -ne 0 -or $peer.ExitCode -ne 0) {
        throw "Managed WinDivert lab failed: tests=$testExit peer=$($peer.ExitCode)"
    }
    Write-Output 'Managed C# WinDivert send/receive exchanged exact packets with isolated Wintun.'
}
finally {
    $env:VOWIN_WINDIVERT_LAB_DLL = $priorDll
    if ($peerStarted -and -not $peer.HasExited) {
        if (-not $peer.WaitForExit(45000)) {
            $peer.Kill()
            $peer.WaitForExit(5000) | Out-Null
            Write-Warning 'Forced the isolated peer to stop after its own cleanup deadline; inspect the temporary adapter.'
        }
    }
    $peer.Dispose()
}
