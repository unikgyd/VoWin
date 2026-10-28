param(
    [Parameter(Mandatory = $true)] [string] $InterfaceName,
    [Parameter(Mandatory = $true)] [string] $ReportPath
)

$ErrorActionPreference = 'Stop'
$connectionAttempted = $false
Start-Transcript -LiteralPath $ReportPath -Force | Out-Null
try {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'An elevated Windows administrator token is required for Additional PDP Context.'
    }

    $profiles = & netsh mbn show profiles "interface=$InterfaceName" 'purpose=ims' 2>&1
    $profileNames = @($profiles | Where-Object { $_ -match '^\s*[0-9a-fA-F]{8}(-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}\s*$' } |
        ForEach-Object { $_.Trim() })
    if ($profileNames.Count -ne 1) {
        throw 'Expected exactly one existing IMS-purpose profile on this interface.'
    }
    $profile = & netsh mbn show profile "interface=$InterfaceName" "name=$($profileNames[0])" 2>&1
    if (($profile -join "`n") -notmatch 'Access Point Name\s*:\s*ims\b' -or
        ($profile -join "`n") -notmatch 'Additional PDP Context\s*:\s*Yes\b') {
        throw 'The selected existing profile is not an IMS Additional PDP Context.'
    }

    $before = & netsh mbn show connection "interface=$InterfaceName" 2>&1
    $before | Out-Host
    if (($before -join "`n") -notmatch 'Interface State\s*:\s*Not connected') {
        throw 'The physical Windows mobile-broadband interface is already connected; refusing to interrupt it.'
    }

    $connectionAttempted = $true
    $result = & netsh mbn connect "interface=$InterfaceName" 'connmode=name' "name=$($profileNames[0])" 2>&1
    $result | Out-Host
    Start-Sleep -Seconds 5
    & netsh mbn show connection "interface=$InterfaceName"
    Get-NetAdapter | Where-Object {
        $_.InterfaceDescription -match 'Quectel|Qualcomm'
    } | Select-Object Name, InterfaceDescription, Status, InterfaceIndex | Format-Table -AutoSize
    Get-NetIPAddress | Where-Object {
        $_.InterfaceAlias -eq $InterfaceName -and $_.AddressFamily -eq 'IPv6'
    } | Select-Object InterfaceAlias, IPAddress, PrefixLength, AddressState | Format-Table -AutoSize
}
finally {
    if ($connectionAttempted) {
        Write-Output 'Disconnecting any IMS Additional PDP context opened by this probe.'
        & netsh mbn disconnect "interface=$InterfaceName"
        & netsh mbn show connection "interface=$InterfaceName"
    }
    Stop-Transcript | Out-Null
}
