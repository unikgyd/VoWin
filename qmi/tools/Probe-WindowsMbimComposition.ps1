param([string[]] $AtPorts = @('COM9', 'COM6'))

$ErrorActionPreference = 'Stop'
$modeChangeIssued = $false

function Invoke-At {
    param([Parameter(Mandatory = $true)] [string] $Command)

    foreach ($name in $AtPorts) {
        $port = [System.IO.Ports.SerialPort]::new(
            $name, 115200, [System.IO.Ports.Parity]::None, 8,
            [System.IO.Ports.StopBits]::One)
        try {
            $port.ReadTimeout = 1500
            $port.WriteTimeout = 1500
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
            if ($response -match '(?m)^(OK|ERROR)\s*$') { return $response }
        }
        catch [System.Exception] {
            # One of the known AT ports may be busy or re-enumerating.
        }
        finally {
            $port.Dispose()
        }
    }
    throw "No known AT port answered $Command."
}

function Wait-UsbnetMode {
    param([Parameter(Mandatory = $true)] [int] $Mode)

    # The old AT port can briefly answer after CFUN before USB re-enumeration.
    Start-Sleep -Seconds 20
    $deadline = [DateTime]::UtcNow.AddSeconds(120)
    do {
        try {
            $reply = Invoke-At 'AT+QCFG="usbnet"'
            if ($reply -match ('\+QCFG:\s*"usbnet",' + $Mode + '\b')) { return }
        }
        catch [System.Exception] {
            # Keep waiting for the new USB composition.
        }
        Start-Sleep -Seconds 2
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "USB network mode $Mode was not observed after re-enumeration."
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'An elevated Windows administrator token is required.'
}
$baseline = Invoke-At 'AT+QCFG="usbnet"'
if ($baseline -notmatch '\+QCFG:\s*"usbnet",0\b') {
    throw 'Expected the known baseline usbnet=0; refusing to change an unknown mode.'
}
$ims = Invoke-At 'AT+QCFG="ims"'
if ($ims -notmatch '\+QCFG:\s*"ims",1,1') {
    throw 'Expected internal IMS to be enabled and registered before the probe.'
}

try {
    Write-Output 'Switching USB network composition from RmNet/QMI to MBIM.'
    $modeChangeIssued = $true
    $reply = Invoke-At 'AT+QCFG="usbnet",2'
    if ($reply -notmatch '(?m)^OK\s*$') { throw 'Modem rejected usbnet=2.' }
    $reply = Invoke-At 'AT+CFUN=1,1'
    if ($reply -notmatch '(?m)^OK\s*$') { throw 'Modem did not acknowledge the MBIM reboot.' }
    Wait-UsbnetMode 2
    Start-Sleep -Seconds 10

    Write-Output 'Present modem USB and network devices in MBIM composition:'
    Get-PnpDevice -PresentOnly | Where-Object {
        $_.InstanceId -match 'VID_2C7C&PID_0125' -or
        $_.FriendlyName -match 'Mobile Broadband|MBIM'
    } | Select-Object Class, FriendlyName, Status | Format-Table -AutoSize
    Get-NetAdapter -IncludeHidden | Where-Object {
        $_.InterfaceDescription -match 'Quectel|MBIM|Mobile Broadband'
    } | Select-Object Name, InterfaceDescription, Status, InterfaceIndex | Format-Table -AutoSize

    $interfaces = & netsh mbn show interfaces 2>&1
    $interfaceNames = @($interfaces | Where-Object { $_ -match '^\s*Name\s*:\s*(.+?)\s*$' } |
        ForEach-Object { [regex]::Match($_, '^\s*Name\s*:\s*(.+?)\s*$').Groups[1].Value })
    Write-Output "Windows MBN interface count: $($interfaceNames.Count)"
    foreach ($interfaceName in $interfaceNames) {
        Write-Output "MBN interface: $interfaceName"
        & netsh mbn show capability "interface=$interfaceName" |
            Select-String 'Maximum activation contexts|Device type|Data class'
    }
}
finally {
    if ($modeChangeIssued) {
        Write-Output 'Restoring RmNet/QMI usbnet=0.'
        $reply = Invoke-At 'AT+QCFG="usbnet",0'
        if ($reply -notmatch '(?m)^OK\s*$') { throw 'Modem rejected usbnet=0 restoration.' }
        $reply = Invoke-At 'AT+CFUN=1,1'
        if ($reply -notmatch '(?m)^OK\s*$') { throw 'Modem did not acknowledge the restoration reboot.' }
        Wait-UsbnetMode 0
        Write-Output 'usbnet=0 restored.'
    }
}
