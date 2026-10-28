param(
    [Parameter(Mandatory = $true)] [ValidatePattern('^COM[0-9]{1,3}$')] [string] $AtPort,
    [Parameter(Mandatory = $true)] [ValidatePattern('^[0-9a-fA-F]{64}$')] [string] $ExpectedImeiHash,
    [Parameter(Mandatory = $true)] [ValidatePattern('^VoWin IMS Restore [0-9a-f]{16}$')] [string] $TaskName,
    [Parameter(Mandatory = $true)] [ValidateRange(1, 2147483647)] [int] $OwnerProcessId,
    [Parameter(Mandatory = $true)] [long] $OwnerStartTicks,
    [switch] $DryRun
)

# A Task Scheduler startup guard for an IMS PPP handoff interrupted by a full
# Windows reboot. The process watchdog cannot run across an OS shutdown.
$ErrorActionPreference = 'Stop'
$recoveryRoot = Join-Path $env:ProgramData 'VoWin\ImsRecovery'
$logPath = Join-Path $recoveryRoot 'boot-recovery.log'

function Write-RecoveryLog {
    param([string] $Message)
    Add-Content -LiteralPath $logPath -Value "$(Get-Date -Format o) $Message"
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

function Wait-MatchingModem {
    $deadline = [DateTime]::UtcNow.AddMinutes(3)
    do {
        try {
            $response = Invoke-RecoveryAt 'AT+GSN'
        }
        catch [System.Exception] {
            Start-Sleep -Seconds 2
            continue
        }
        $matches = @([regex]::Matches($response, '(?m)^\s*(\d{15})\s*$'))
        if ($matches.Count -eq 1) {
            $sha = [System.Security.Cryptography.SHA256]::Create()
            try {
                $bytes = [Text.Encoding]::ASCII.GetBytes($matches[0].Groups[1].Value)
                $hash = [BitConverter]::ToString($sha.ComputeHash($bytes)).Replace('-', '')
            }
            finally { $sha.Dispose() }
            if ($hash -ieq $ExpectedImeiHash) { return }
            throw 'AT port belongs to a different modem; refusing IMS change.'
        }
        Start-Sleep -Seconds 2
    } while ([DateTime]::UtcNow -lt $deadline)
    throw 'The original modem was not available at the expected AT port after boot.'
}

function Wait-ImsState {
    param([string] $Expected = '', [int] $Seconds = 120)
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
    throw "IMS mode $Expected was not observed within $Seconds seconds."
}

function Disarm-BootTask {
    if ($DryRun) { return }
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction Stop
    Write-RecoveryLog "DISARMED $TaskName after verified ims=1,1."
}

try {
    $owner = Get-Process -Id $OwnerProcessId -ErrorAction SilentlyContinue
    if ($owner -and $owner.StartTime.ToUniversalTime().Ticks -eq $OwnerStartTicks) {
        Write-RecoveryLog 'Owner is still live; boot recovery did not touch IMS.'
        exit 0
    }
    if (Get-Process -Name 'RasImsPppProbe' -ErrorAction SilentlyContinue) {
        throw 'A RAS IMS lease process is still running; refusing recovery mutation.'
    }
    $pppAdapter = Get-NetAdapter -Name 'VoWin IMS PPP Probe' -ErrorAction SilentlyContinue
    if ($pppAdapter -and $pppAdapter.Status -eq 'Up') {
        throw 'Windows IMS PPP is still connected; refusing recovery mutation.'
    }

    Wait-MatchingModem
    Write-RecoveryLog 'Modem identity matched the handoff guard.'
    $state = Wait-ImsState '' 120
    Write-RecoveryLog "Observed ims=$state after Windows boot."
    if ($state -eq '1,1') {
        Disarm-BootTask
        exit 0
    }
    if ($state -notin @('2,0', '1,0')) {
        throw "Unexpected IMS mode $state; refusing recovery mutation."
    }
    if ($DryRun) {
        Write-RecoveryLog 'DRY RUN: restoration would be attempted.'
        exit 0
    }

    for ($attempt = 1; $attempt -le 2; $attempt++) {
        $set = Invoke-RecoveryAt 'AT+QCFG="ims",1'
        if ($set -notmatch '(?m)^OK\s*$') { throw "IMS restore command failed on attempt $attempt." }
        $reboot = Invoke-RecoveryAt 'AT+CFUN=1,1'
        if ($reboot -notmatch '(?m)^OK\s*$') { throw "Modem reboot failed on attempt $attempt." }
        Write-RecoveryLog "Restore attempt $attempt acknowledged; waiting for modem reboot."
        Start-Sleep -Seconds 20
        try {
            Wait-ImsState '1,1' 120 | Out-Null
            Write-RecoveryLog 'RESTORED ims=1,1 after Windows boot.'
            Disarm-BootTask
            exit 0
        }
        catch {
            if ($attempt -eq 2) { throw }
            Write-RecoveryLog 'First restore did not reach ims=1,1; retrying once.'
        }
    }
}
catch {
    Write-RecoveryLog "BOOT RECOVERY FAILED: $($_.Exception.Message)"
    exit 1
}
