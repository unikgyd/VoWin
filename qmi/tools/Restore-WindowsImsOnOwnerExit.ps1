param(
    [Parameter(Mandatory = $true)] [int] $OwnerProcessId,
    [Parameter(Mandatory = $true)] [long] $OwnerStartTicks,
    [string] $AtPort = 'COM6',
    [Parameter(Mandatory = $true)] [ValidatePattern('^\d{15}$')] [string] $ExpectedImei,
    [Parameter(Mandatory = $true)] [string] $DonePath,
    [Parameter(Mandatory = $true)] [string] $LogPath
)

# Independent safety net for Probe-WindowsImsPppHandoff.ps1. The owner verifies
# ims=1,1 before launching us, then waits for our WATCHING marker before it may
# change the modem. We only touch AT after that exact owner process exits.
$ErrorActionPreference = 'Stop'
$resolvedLog = [System.IO.Path]::GetFullPath($LogPath)
$resolvedDone = [System.IO.Path]::GetFullPath($DonePath)
$tempRoot = [System.IO.Path]::GetFullPath($env:TEMP).TrimEnd('\') + '\'
if (-not $resolvedLog.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or
    -not $resolvedDone.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Recovery files must stay inside the Windows temporary directory.'
}

function Write-RecoveryLog {
    param([string] $Message)
    Add-Content -LiteralPath $resolvedLog -Value "$(Get-Date -Format o) $Message"
}

function Invoke-RecoveryAt {
    param([string] $Command)
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
    finally { $port.Dispose() }
}

function Wait-RecoveryImsState {
    param([string] $Expected, [int] $Seconds = 120)
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    do {
        try {
            $response = Invoke-RecoveryAt 'AT+QCFG="ims"'
            if ($response -match '\+QCFG:\s*"ims",([012]),([01])') {
                $state = "$($Matches[1]),$($Matches[2])"
                if (-not $Expected -or $state -eq $Expected) { return $state }
            }
        }
        catch [System.Exception] { }
        Start-Sleep -Seconds 2
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Modem IMS state $Expected was not observed within $Seconds seconds."
}

function Wait-RecoveryImei {
    $deadline = [DateTime]::UtcNow.AddSeconds(180)
    do {
        try {
            $response = Invoke-RecoveryAt 'AT+GSN'
            $matches = @([regex]::Matches($response, '(?m)^\s*(\d{15})\s*$'))
            if ($matches.Count -eq 1) { return $matches[0].Groups[1].Value }
        }
        catch [System.Exception] { }
        Start-Sleep -Seconds 2
    } while ([DateTime]::UtcNow -lt $deadline)
    throw 'The original modem IMEI could not be read after owner exit.'
}

function Restore-RecoveryIms {
    for ($attempt = 1; $attempt -le 2; $attempt++) {
        $set = Invoke-RecoveryAt 'AT+QCFG="ims",1'
        if ($set -notmatch '(?m)^OK\s*$') { throw "IMS restoration command failed on attempt $attempt." }
        $reboot = Invoke-RecoveryAt 'AT+CFUN=1,1'
        if ($reboot -notmatch '(?m)^OK\s*$') { throw "CFUN reboot failed on attempt $attempt." }
        Write-RecoveryLog "Restore attempt $attempt acknowledged; waiting for modem reboot."
        Start-Sleep -Seconds 20
        try {
            Wait-RecoveryImsState '1,1' 120 | Out-Null
            Write-RecoveryLog 'RESTORED ims=1,1'
            return
        }
        catch {
            if ($attempt -eq 2) { throw }
            Write-RecoveryLog 'First reboot did not reach ims=1,1; retrying once.'
        }
    }
}

try {
    $owner = Get-Process -Id $OwnerProcessId -ErrorAction SilentlyContinue
    if ($owner -and $owner.StartTime.ToUniversalTime().Ticks -eq $OwnerStartTicks) {
        Write-RecoveryLog "WATCHING owner PID=$OwnerProcessId"
        while (-not $owner.WaitForExit(2000)) {
            if (Test-Path -LiteralPath $resolvedDone -PathType Leaf) {
                Remove-Item -LiteralPath $resolvedDone -Force
                Write-RecoveryLog 'DISARMED after owner verified ims=1,1.'
                exit 0
            }
        }
    }
    else {
        Write-RecoveryLog 'WATCHING owner already exited; checking modem.'
    }
    if (Test-Path -LiteralPath $resolvedDone -PathType Leaf) {
        Remove-Item -LiteralPath $resolvedDone -Force
        Write-RecoveryLog 'DISARMED after owner exit.'
        exit 0
    }
    $observedImei = Wait-RecoveryImei
    if ($observedImei -ne $ExpectedImei) {
        throw 'AT port now belongs to a different modem IMEI; refusing recovery mutation.'
    }
    Write-RecoveryLog 'The modem IMEI still matches the original owner.'
    $state = Wait-RecoveryImsState '' 180
    Write-RecoveryLog "Observed ims=$state after owner exit."
    if ($state -eq '1,1') {
        Write-RecoveryLog 'No restoration needed.'
        exit 0
    }
    if ($state -notin @('2,0', '1,0')) {
        throw "Unexpected IMS mode $state; refusing an unverified configuration change."
    }
    Restore-RecoveryIms
    exit 0
}
catch {
    Write-RecoveryLog "RECOVERY FAILED: $($_.Exception.Message)"
    exit 1
}
