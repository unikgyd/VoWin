param(
    [ValidatePattern('^COM[0-9]{1,3}$')] [string] $AtPort = 'COM6',
    [string] $ProbeExecutable,
    [ValidateRange(15, 300)] [int] $HoldSeconds = 60,
    [switch] $IntegratedRouteProbe,
    [switch] $ManagedLease,
    [ValidateRange(15, 3600)] [int] $LeaseMaxSeconds = 3600,
    [switch] $SimulateOwnerExit,
    [switch] $SimulateOuterCrashAfterDisable,
    [switch] $SimulateOuterCrashWithIsolatedPpp,
    [string] $RouteScript = (Join-Path $PSScriptRoot 'Test-WindowsImsPppRouteIsolation.ps1'),
    [string] $NpcapProbeExecutable,
    [string] $WinDivertNetdumpExecutable,
    [string] $WinDivertManagedProbeDll,
    [string] $RecoveryScript = (Join-Path $PSScriptRoot 'Restore-WindowsImsOnOwnerExit.ps1'),
    [string] $CliExecutable = (Join-Path $PSScriptRoot '..\..\voSharp\src\VoSharp.Cli\bin\Debug\net10.0\VoSharp.Cli.exe')
)

$ErrorActionPreference = 'Stop'
# Packaged processes do not import user-installed modules in elevated recovery.
if (Test-Path -LiteralPath (Join-Path $PSScriptRoot '../powershell/pwsh.exe')) {
    $env:PSModulePath = (Join-Path $PSHOME 'Modules') + ';' +
        (Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/Modules')
}
$disableIssued = $false
if (-not $ProbeExecutable) {
    $packagedProbe = Join-Path $PSScriptRoot 'RasImsPppProbe.exe'
    $ProbeExecutable = if (Test-Path -LiteralPath $packagedProbe -PathType Leaf) {
        $packagedProbe
    }
    else {
        Join-Path $env:TEMP 'VoWinRasImsPppProbe.exe'
    }
}

function Invoke-ModemAt {
    param([Parameter(Mandatory = $true)] [string] $Command)

    $port = [System.IO.Ports.SerialPort]::new($AtPort, 115200)
    try {
        $port.ReadTimeout = 2000
        $port.WriteTimeout = 2000
        $port.Open()
        $port.DiscardInBuffer()
        $port.Write("$Command`r")
        $response = ''
        $deadline = [DateTime]::UtcNow.AddSeconds(4)
        do {
            Start-Sleep -Milliseconds 200
            $response += $port.ReadExisting()
            if ($response -match '(?m)^(OK|ERROR)\s*$') { break }
        } while ([DateTime]::UtcNow -lt $deadline)
        return $response
    }
    finally {
        $port.Dispose()
    }
}

function Wait-ImsMode {
    param([int] $Mode)

    $expected = if ($Mode -eq 1) { '\+QCFG:\s*"ims",1,1' } else { '\+QCFG:\s*"ims",2,0' }
    $deadline = [DateTime]::UtcNow.AddSeconds(120)
    do {
        try {
            $response = Invoke-ModemAt 'AT+QCFG="ims"'
            if ($response -match $expected) { return }
        }
        catch [System.Exception] {
            # The AT port disappears briefly during the modem reboot.
        }
        Start-Sleep -Seconds 2
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Modem did not return in IMS mode $Mode within 120 seconds."
}

function Restart-Modem {
    $response = Invoke-ModemAt 'AT+CFUN=1,1'
    if ($response -notmatch '(?m)^OK\s*$') { throw 'Modem did not acknowledge CFUN reboot.' }
    Start-Sleep -Seconds 20
}

function Wait-RasModem {
    $deadline = [DateTime]::UtcNow.AddSeconds(120)
    do {
        & $ProbeExecutable --check-modem | Out-Null
        if ($LASTEXITCODE -eq 0) { return }
        Start-Sleep -Seconds 5
    } while ([DateTime]::UtcNow -lt $deadline)
    throw 'Quectel RAS modem did not reappear within 120 seconds; PPP dial was not attempted.'
}

function Convert-DottedIpv6 {
    param([Parameter(Mandatory = $true)] [string] $Text)
    $parts = $Text.Trim().Split('.')
    if ($parts.Count -ne 16) { throw "Expected sixteen decimal IPv6 octets: $Text" }
    $bytes = [byte[]]::new(16)
    for ($i = 0; $i -lt 16; $i++) {
        [byte] $octet = 0
        if (-not [byte]::TryParse($parts[$i], [ref]$octet)) {
            throw "Invalid decimal IPv6 octet in $Text"
        }
        $bytes[$i] = $octet
    }
    return [System.Net.IPAddress]::new($bytes)
}

function Get-CurrentImsPcscf {
    $response = Invoke-ModemAt 'AT+CGCONTRDP=5'
    $line = @($response -split "`r?`n" | Where-Object { $_ -match '^\+CGCONTRDP:\s*' })
    if ($line.Count -ne 1) { throw 'CID 5 did not return one runtime IMS context.' }
    $fields = @(($line[0] -replace '^\+CGCONTRDP:\s*', '').Split(',') |
        ForEach-Object { $_.Trim().Trim('"') })
    if ($fields.Count -lt 9 -or $fields[0] -ne '5' -or $fields[2] -ne 'ims') {
        throw 'CID 5 runtime APN or P-CSCF fields are incomplete.'
    }
    $modemLocal = Convert-DottedIpv6 $fields[3]
    $interface = @(Get-NetIPInterface -AddressFamily IPv6 -InterfaceAlias 'VoWin IMS PPP Probe' -ErrorAction SilentlyContinue)
    if ($interface.Count -ne 1 -or $interface[0].ConnectionState -ne 'Connected') {
        throw 'Controlled IMS PPP is no longer connected.'
    }
    $pppAddress = @(Get-NetIPAddress -InterfaceIndex $interface[0].InterfaceIndex -AddressFamily IPv6 |
        Where-Object { $_.AddressState -eq 'Preferred' -and $_.IPAddress -notmatch '^fe80:' })
    if ($pppAddress.Count -ne 1) { throw 'Expected one preferred IMS PPP IPv6 address.' }
    $hostBytes = ([System.Net.IPAddress]::Parse($pppAddress[0].IPAddress)).GetAddressBytes()
    $modemBytes = $modemLocal.GetAddressBytes()
    for ($i = 0; $i -lt 8; $i++) {
        if ($hostBytes[$i] -ne $modemBytes[$i]) {
            throw 'Windows PPP address does not share the modem IMS /64.'
        }
    }
    return @((Convert-DottedIpv6 $fields[7]).ToString(),
             (Convert-DottedIpv6 $fields[8]).ToString())
}

function Wait-PppConnected {
    param([System.Diagnostics.Process] $Process)
    $deadline = [DateTime]::UtcNow.AddSeconds(90)
    $readLine = $Process.StandardOutput.ReadLineAsync()
    do {
        if ($readLine.Wait(2000)) {
            $line = $readLine.GetAwaiter().GetResult()
            if ($null -eq $line) { throw 'PPP probe closed stdout before RAS finished connecting.' }
            Write-Output $line
            if ($line -like 'RAS connected; holding*') {
                $interface = @(Get-NetIPInterface -AddressFamily IPv6 -InterfaceAlias 'VoWin IMS PPP Probe' -ErrorAction SilentlyContinue)
                if ($interface.Count -ne 1 -or $interface[0].ConnectionState -ne 'Connected') {
                    throw 'RAS reported Connected before the expected IMS PPP IPv6 interface was ready.'
                }
                Start-Sleep -Seconds 2
                return
            }
            $readLine = $Process.StandardOutput.ReadLineAsync()
        }
        if ($Process.HasExited) { throw "PPP probe exited before connection: $($Process.ExitCode)" }
    } while ([DateTime]::UtcNow -lt $deadline)
    throw 'RAS did not report a completed PPP connection within 90 seconds.'
}

function Get-ModemImei {
    $response = Invoke-ModemAt 'AT+GSN'
    $matches = @([regex]::Matches($response, '(?m)^\s*(\d{15})\s*$'))
    if ($matches.Count -ne 1) { throw 'Could not read one stable 15-digit modem IMEI.' }
    return $matches[0].Groups[1].Value
}

function Start-RecoveryWatchdog {
    param([string] $DonePath, [string] $LogPath, [string] $ExpectedImei)
    $owner = [System.Diagnostics.Process]::GetCurrentProcess()
    $self = $owner.MainModule.FileName
    $arguments = @(
        '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', ('"' + $RecoveryScript + '"'),
        '-OwnerProcessId', [string]$owner.Id,
        '-OwnerStartTicks', [string]$owner.StartTime.ToUniversalTime().Ticks,
        '-AtPort', $AtPort,
        '-ExpectedImei', $ExpectedImei,
        '-DonePath', ('"' + $DonePath + '"'),
        '-LogPath', ('"' + $LogPath + '"')
    )
    $watchdog = Start-Process -FilePath $self -ArgumentList $arguments -WindowStyle Hidden -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    do {
        if ($watchdog.HasExited) { throw 'IMS recovery watchdog exited before arming.' }
        if (Test-Path -LiteralPath $LogPath -PathType Leaf) {
            $log = Get-Content -LiteralPath $LogPath -Raw
            if ($log -match 'WATCHING owner') { return $watchdog }
            if ($log -match 'RECOVERY FAILED') { throw $log }
        }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $deadline)
    throw 'IMS recovery watchdog did not arm within ten seconds.'
}

function Start-BootRecoveryGuard {
    param([string] $ExpectedImei)

    $source = Join-Path $PSScriptRoot 'Restore-WindowsImsAfterBoot.ps1'
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
        throw "IMS boot recovery script not found: $source"
    }
    $root = Join-Path $env:ProgramData 'VoWin\ImsRecovery'
    New-Item -ItemType Directory -Path $root -Force | Out-Null
    $acl = Get-Acl -LiteralPath $root
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($rule in @($acl.Access)) { $acl.RemoveAccessRuleAll($rule) }
    $inheritance = [System.Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit'
    $propagation = [System.Security.AccessControl.PropagationFlags]::None
    $allow = [System.Security.AccessControl.AccessControlType]::Allow
    foreach ($sidText in @('S-1-5-18', 'S-1-5-32-544')) {
        $sid = [System.Security.Principal.SecurityIdentifier]::new($sidText)
        $rule = [System.Security.AccessControl.FileSystemAccessRule]::new(
            $sid, [System.Security.AccessControl.FileSystemRights]::FullControl,
            $inheritance, $propagation, $allow)
        $acl.AddAccessRule($rule)
    }
    Set-Acl -LiteralPath $root -AclObject $acl

    $installed = Join-Path $root 'Restore-WindowsImsAfterBoot.ps1'
    Copy-Item -LiteralPath $source -Destination $installed -Force
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $hash = [BitConverter]::ToString(
            $sha.ComputeHash([Text.Encoding]::ASCII.GetBytes($ExpectedImei))).Replace('-', '').ToLowerInvariant()
    }
    finally { $sha.Dispose() }
    $taskName = "VoWin IMS Restore $($hash.Substring(0, 16))"
    $owner = [System.Diagnostics.Process]::GetCurrentProcess()
    $powershellExe = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $arguments = '-NoProfile -ExecutionPolicy Bypass -File "{0}" -AtPort {1} -ExpectedImeiHash {2} -TaskName "{3}" -OwnerProcessId {4} -OwnerStartTicks {5}' -f
        $installed, $AtPort, $hash, $taskName, $owner.Id, $owner.StartTime.ToUniversalTime().Ticks

    $existing = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
    if ($existing) {
        if (@($existing.Actions).Count -ne 1 -or
            $existing.Actions[0].Execute -ine $powershellExe -or
            $existing.Actions[0].Arguments -notlike "*Restore-WindowsImsAfterBoot.ps1*") {
            throw "An unrelated scheduled task already uses $taskName."
        }
        Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
    }
    $action = New-ScheduledTaskAction -Execute $powershellExe -Argument $arguments
    $trigger = New-ScheduledTaskTrigger -AtStartup
    $principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
    $settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -ExecutionTimeLimit (New-TimeSpan -Minutes 10) `
        -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 2)
    Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Principal $principal `
        -Settings $settings -Description 'Restore modem IMS after an interrupted VoWin PPP handoff.' | Out-Null
    if (-not (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue)) {
        throw 'IMS boot recovery task did not become visible after registration.'
    }
    return $taskName
}

function Stop-BootRecoveryGuard {
    param([string] $TaskName)
    if ($TaskName -and (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue)) {
        Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
    }
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'An elevated Windows administrator token is required.'
}
if (-not (Test-Path -LiteralPath $ProbeExecutable -PathType Leaf)) {
    throw "PPP probe executable not found: $ProbeExecutable"
}
if ($ManagedLease -and -not $IntegratedRouteProbe) {
    throw 'A managed IMS bearer requires -IntegratedRouteProbe.'
}
if ($IntegratedRouteProbe -and -not (Test-Path -LiteralPath $RouteScript -PathType Leaf)) {
    throw 'Integrated route probe requires the route script.'
}
if ($PSBoundParameters.ContainsKey('CliExecutable') -and
    -not (Test-Path -LiteralPath $CliExecutable -PathType Leaf)) {
    throw "Explicit VoSharp CLI executable not found: $CliExecutable"
}
if ($SimulateOwnerExit -and -not $IntegratedRouteProbe) {
    throw 'Owner-exit simulation requires the integrated route probe.'
}
if ($SimulateOuterCrashWithIsolatedPpp -and -not $IntegratedRouteProbe) {
    throw 'Isolated-PPP crash simulation requires the integrated route probe.'
}
if (($SimulateOuterCrashAfterDisable -or $SimulateOuterCrashWithIsolatedPpp) -and
    $env:VOWIN_IMS_ALLOW_CRASH_PROBE -ne '1') {
    throw 'Deliberate owner-crash tests require VOWIN_IMS_ALLOW_CRASH_PROBE=1.'
}
if (-not (Test-Path -LiteralPath $RecoveryScript -PathType Leaf)) {
    throw "IMS recovery watchdog not found: $RecoveryScript"
}
if ($NpcapProbeExecutable -and
    -not (Test-Path -LiteralPath $NpcapProbeExecutable -PathType Leaf)) {
    throw "Npcap interface probe not found: $NpcapProbeExecutable"
}
$before = Invoke-ModemAt 'AT+QCFG="ims"'
if ($before -notmatch '\+QCFG:\s*"ims",1,1') {
    throw 'Expected baseline IMS mode 1,1; refusing to change an unknown setting.'
}
$usbnet = Invoke-ModemAt 'AT+QCFG="usbnet"'
if ($usbnet -notmatch '\+QCFG:\s*"usbnet",0') {
    throw 'Expected baseline RmNet usbnet=0.'
}
$profile = Invoke-ModemAt 'AT+CGDCONT?'
if ($profile -notmatch '(?m)^\+CGDCONT:\s*5,"IPV4V6","ims"') {
    throw 'Expected the known CID 5 IPV4V6 ims profile.'
}
$wwan = @(Get-NetAdapter -InterfaceDescription 'Quectel Wireless Ethernet Adapter' -ErrorAction SilentlyContinue)
if ($wwan.Count -ne 1 -or $wwan[0].Status -ne 'Disconnected') {
    throw 'The sole Quectel Windows WWAN adapter is not confirmed disconnected.'
}
$expectedImei = Get-ModemImei

$pppProcess = $null
$pppStarted = $false
$watchdog = $null
$bootTaskName = Start-BootRecoveryGuard $expectedImei
$recoveryId = [guid]::NewGuid().ToString('N')
$recoveryDone = Join-Path $env:TEMP "VoWinImsRecovery-$recoveryId.done"
$recoveryLog = Join-Path $env:TEMP "VoWinImsRecovery-$recoveryId.log"
try { $watchdog = Start-RecoveryWatchdog $recoveryDone $recoveryLog $expectedImei }
catch {
    Stop-BootRecoveryGuard $bootTaskName
    throw
}
Write-Output "IMS recovery watchdog armed (PID $($watchdog.Id))."
try {
    Write-Output 'Temporarily disabling modem-internal IMS for PPP CID 5.'
    $disableIssued = $true
    $response = Invoke-ModemAt 'AT+QCFG="ims",2'
    if ($response -notmatch '(?m)^OK\s*$') { throw 'Modem rejected QCFG ims=2.' }
    Restart-Modem
    Wait-ImsMode 2
    Wait-RasModem
    if ($SimulateOuterCrashAfterDisable) {
        [Environment]::Exit(92) # Lab-only: bypass finally to exercise watchdog.
    }
    Write-Output 'Dialing CID 5 with CCP disabled; no persistent RAS entry.'
    if ($IntegratedRouteProbe) {
        $start = [System.Diagnostics.ProcessStartInfo]::new()
        $start.FileName = $ProbeExecutable
        $start.ArgumentList.Add('--no-ccp')
        $start.ArgumentList.Add('--until-stop')
        $start.ArgumentList.Add("--max-seconds=$(if ($ManagedLease) { $LeaseMaxSeconds } else { $HoldSeconds })")
        $start.UseShellExecute = $false
        $start.RedirectStandardInput = $true
        $start.RedirectStandardOutput = $true
        $start.CreateNoWindow = $true
        $start.WindowStyle = [System.Diagnostics.ProcessWindowStyle]::Hidden
        $pppProcess = [System.Diagnostics.Process]::new()
        $pppProcess.StartInfo = $start
        $pppStarted = $pppProcess.Start()
        if (-not $pppStarted) { throw 'Could not start the controlled PPP lease process.' }
        Wait-PppConnected $pppProcess
        $pcscf = @(Get-CurrentImsPcscf)
        Write-Output "Automatically detected IMS P-CSCF: $($pcscf -join ', ')"
        $routeArguments = @{
            PcscfAddresses = $pcscf
            AtPort = $AtPort
            SimulateOuterCrash = $SimulateOuterCrashWithIsolatedPpp
            WaitForStop = $ManagedLease
            MaxLeaseSeconds = $LeaseMaxSeconds
        }
        if ($ManagedLease) { $routeArguments.PppLeaseProcess = $pppProcess }
        if (-not $ManagedLease -and (Test-Path -LiteralPath $CliExecutable -PathType Leaf)) {
            $routeArguments.CliExecutable = $CliExecutable
        }
        if ($NpcapProbeExecutable) { $routeArguments.NpcapProbeExecutable = $NpcapProbeExecutable }
        if ($WinDivertNetdumpExecutable) { $routeArguments.WinDivertNetdumpExecutable = $WinDivertNetdumpExecutable }
        if ($WinDivertManagedProbeDll) { $routeArguments.WinDivertManagedProbeDll = $WinDivertManagedProbeDll }
        & $RouteScript @routeArguments
        if (-not $SimulateOwnerExit) {
            $pppProcess.StandardInput.WriteLine('STOP')
        }
        $pppProcess.StandardInput.Close()
        if (-not $pppProcess.WaitForExit(90000)) { throw 'PPP lease did not hang up within 90 seconds of STOP.' }
        Write-Output "PPP probe exit code: $($pppProcess.ExitCode)"
        if ($pppProcess.ExitCode -ne 0) { throw 'PPP probe failed after route isolation.' }
    }
    else {
        & $ProbeExecutable --no-ccp "--hold-seconds=$HoldSeconds"
        Write-Output "PPP probe exit code: $LASTEXITCODE"
    }
}
finally {
    if ($pppProcess) {
        if ($pppStarted -and -not $pppProcess.HasExited) {
            try {
                $pppProcess.StandardInput.WriteLine('STOP')
                $pppProcess.StandardInput.Close()
            }
            catch { }
            if (-not $pppProcess.WaitForExit(90000)) {
                Stop-Process -Id $pppProcess.Id -Force -ErrorAction SilentlyContinue
                Write-Warning 'Stopped only the controlled PPP lease process after failed graceful hangup.'
            }
        }
        if ($pppStarted -and $pppProcess.HasExited) {
            while ($null -ne ($line = $pppProcess.StandardOutput.ReadLine())) {
                Write-Output $line
            }
        }
        $pppProcess.Dispose()
    }
    if ($disableIssued) {
        Write-Output 'Restoring modem-internal IMS mode 1.'
        Wait-ImsMode 2
        $response = Invoke-ModemAt 'AT+QCFG="ims",1'
        if ($response -notmatch '(?m)^OK\s*$') { throw 'Modem rejected IMS restoration.' }
        Restart-Modem
        try {
            Wait-ImsMode 1
        }
        catch {
            # This module has returned with ims=1,0 after the first reboot.
            # Reassert the original setting and reboot once more before failing.
            Write-Warning 'IMS configured but not active after first restore; retrying one controlled reboot.'
            $response = Invoke-ModemAt 'AT+QCFG="ims",1'
            if ($response -notmatch '(?m)^OK\s*$') { throw 'Modem rejected IMS restoration retry.' }
            Restart-Modem
            Wait-ImsMode 1
        }
        Write-Output 'IMS mode 1,1 restored after reboot.'
    }
    try { Stop-BootRecoveryGuard $bootTaskName }
    catch { Write-Warning "IMS boot recovery task could not be disarmed: $($_.Exception.Message)" }
    if ($watchdog) {
        New-Item -ItemType File -Path $recoveryDone -ErrorAction Stop | Out-Null
        if (-not $watchdog.WaitForExit(10000)) {
            Write-Warning "IMS recovery watchdog did not acknowledge disarm; log: $recoveryLog"
        }
        else {
            $watchdogLog = Get-Content -LiteralPath $recoveryLog -ErrorAction SilentlyContinue
            $watchdogLog | Write-Output
            Remove-Item -LiteralPath $recoveryLog -Force -ErrorAction SilentlyContinue
        }
        $watchdog.Dispose()
    }
}
