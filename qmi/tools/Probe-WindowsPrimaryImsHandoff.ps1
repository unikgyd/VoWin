param(
    [Parameter(Mandatory = $true)] [string] $InterfaceName,
    [string] $AtPort = 'COM9'
)

$ErrorActionPreference = 'Stop'
$disableIssued = $false

function Invoke-ModemAt {
    param([Parameter(Mandatory = $true)] [string] $Command)

    $port = [System.IO.Ports.SerialPort]::new(
        $AtPort, 115200, [System.IO.Ports.Parity]::None, 8,
        [System.IO.Ports.StopBits]::One)
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

function Wait-ImsSetting {
    param([Parameter(Mandatory = $true)] [int] $Mode)

    $expected = if ($Mode -eq 1) { '\+QCFG:\s*"ims",1,1' } else { '\+QCFG:\s*"ims",2,0' }
    $deadline = [DateTime]::UtcNow.AddSeconds(120)
    do {
        try {
            $response = Invoke-ModemAt 'AT+QCFG="ims"'
            if ($response -match $expected) {
                return $response
            }
        }
        catch [System.Exception] {
            # The AT port may be absent briefly while the modem re-enumerates.
        }
        Start-Sleep -Seconds 2
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Modem did not return with IMS mode $Mode within 120 seconds."
}

function Wait-ModemPort {
    $deadline = [DateTime]::UtcNow.AddSeconds(120)
    do {
        try {
            $response = Invoke-ModemAt 'AT+QCFG="ims"'
            if ($response -match '\+QCFG:\s*"ims",[12],[01]') { return }
        }
        catch [System.Exception] {
            # The modem may still be re-enumerating.
        }
        Start-Sleep -Seconds 2
    } while ([DateTime]::UtcNow -lt $deadline)
    throw 'The modem AT port did not return within 120 seconds.'
}

function Wait-MbnHome {
    $deadline = [DateTime]::UtcNow.AddSeconds(120)
    do {
        $state = & netsh mbn show connection "interface=$InterfaceName" 2>&1
        if (($state -join "`n") -match 'Interface State\s*:\s*Not connected' -and
            ($state -join "`n") -match 'Register State\s*:\s*Home') {
            return
        }
        Start-Sleep -Seconds 2
    } while ([DateTime]::UtcNow -lt $deadline)
    throw 'Windows mobile-broadband interface did not return to Home registration within 120 seconds.'
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'An elevated Windows administrator token is required.'
}

$before = & netsh mbn show connection "interface=$InterfaceName" 2>&1
if (($before -join "`n") -notmatch 'Interface State\s*:\s*Not connected') {
    throw 'The Windows mobile-broadband interface is already connected.'
}
$profiles = & netsh mbn show profiles "interface=$InterfaceName" 'purpose=ims' 2>&1
$profileNames = @($profiles | Where-Object { $_ -match '^\s*[0-9a-fA-F]{8}(-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}\s*$' } |
    ForEach-Object { $_.Trim() })
if ($profileNames.Count -ne 1) { throw 'Expected exactly one existing IMS-purpose profile.' }
$profile = & netsh mbn show profile "interface=$InterfaceName" "name=$($profileNames[0])" 2>&1
$profileText = $profile -join "`n"
if ($profileText -notmatch 'Access Point Name\s*:\s*ims\b' -or
    $profileText -notmatch 'Additional PDP Context\s*:\s*Yes\b' -or
    $profileText -notmatch 'Subscriber Id\s*:\s*(\d{5,20})\b') {
    throw 'The IMS-purpose profile is not an IMS Additional PDP profile with a subscriber ID.'
}
$subscriberId = $Matches[1]
$iccidResponse = Invoke-ModemAt 'AT+QCCID'
if ($iccidResponse -notmatch '\+QCCID:\s*(\d{18,22})\b') {
    throw 'Could not read the SIM ICCID for a temporary IPv6 profile.'
}
$simIccId = $Matches[1]
$original = Invoke-ModemAt 'AT+QCFG="ims"'
if ($original -notmatch '\+QCFG:\s*"ims",1,1') {
    throw 'Expected the known baseline QCFG ims=1,1; refusing to change an unknown configuration.'
}

try {
    Write-Output 'Temporarily disabling modem-internal IMS.'
    $disableIssued = $true
    $response = Invoke-ModemAt 'AT+QCFG="ims",2'
    if ($response -notmatch '(?m)^OK\s*$') { throw 'Modem rejected QCFG ims=2.' }
    $response = Invoke-ModemAt 'AT+CFUN=1,1'
    if ($response -notmatch '(?m)^OK\s*$') { throw 'Modem did not acknowledge the reboot.' }
    # QCFG may answer on the old COM port for a few seconds before USB disappears.
    Start-Sleep -Seconds 20
    Wait-ImsSetting 2 | Out-Null
    Wait-MbnHome
    Start-Sleep -Seconds 10
    Write-Output 'Modem IMS mode 2 verified; probing temporary Windows primary IMS context.'
    & "$PSScriptRoot\Probe-WindowsPrimaryImsPdp.ps1" -InterfaceName $InterfaceName `
        -SubscriberId $subscriberId -SimIccId $simIccId
}
finally {
    if ($disableIssued) {
        Write-Output 'Restoring modem-internal IMS mode 1.'
        Wait-ModemPort
        $response = Invoke-ModemAt 'AT+QCFG="ims",1'
        if ($response -notmatch '(?m)^OK\s*$') { throw 'Modem rejected IMS restoration.' }
        $response = Invoke-ModemAt 'AT+CFUN=1,1'
        if ($response -notmatch '(?m)^OK\s*$') { throw 'Modem did not acknowledge restoration reboot.' }
        Start-Sleep -Seconds 20
        $restored = Wait-ImsSetting 1
        Write-Output 'IMS mode 1 restored after reboot.'
        & netsh mbn show connection "interface=$InterfaceName"
    }
}
