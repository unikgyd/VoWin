param(
    [Parameter(Mandatory = $true)] [string] $InterfaceName,
    [string] $SubscriberId,
    [string] $SimIccId
)

$ErrorActionPreference = 'Stop'
$tempProfile = $null
$connectionAttempted = $false
try {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'An elevated Windows administrator token is required for this IMS probe.'
    }

    if (-not $SubscriberId) {
        $profiles = & netsh mbn show profiles "interface=$InterfaceName" 'purpose=ims' 2>&1
        $profileNames = @($profiles | Where-Object { $_ -match '^\s*[0-9a-fA-F]{8}(-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}\s*$' } |
            ForEach-Object { $_.Trim() })
        if ($profileNames.Count -ne 1) {
            throw 'Expected exactly one existing IMS-purpose profile on this interface.'
        }
        $existing = & netsh mbn show profile "interface=$InterfaceName" "name=$($profileNames[0])" 2>&1
        $existingText = $existing -join "`n"
        if ($existingText -notmatch 'Access Point Name\s*:\s*ims\b' -or
            $existingText -notmatch 'Additional PDP Context\s*:\s*Yes\b' -or
            $existingText -notmatch 'Subscriber Id\s*:\s*(\d{5,20})\b') {
            throw 'The selected existing profile must be an IMS Additional PDP profile with a subscriber ID.'
        }
        $SubscriberId = $Matches[1]
    }
    if ($SubscriberId -notmatch '^\d{5,20}$') { throw 'Invalid subscriber ID.' }
    if ($SimIccId -and $SimIccId -notmatch '^\d{18,22}$') { throw 'Invalid SIM ICCID.' }

    $before = & netsh mbn show connection "interface=$InterfaceName" 2>&1
    $before | Out-Host
    if (($before -join "`n") -notmatch 'Interface State\s*:\s*Not connected') {
        throw 'The physical Windows mobile-broadband interface is already connected.'
    }

    # Windows reports only one activation context on the installed driver. This
    # temporary primary profile tests its data path without changing saved profiles.
    $profileName = 'VoWin-Primary-Ims-Probe-' + [guid]::NewGuid().ToString('N')
    $tempFile = New-TemporaryFile
    $tempProfilePath = [IO.Path]::ChangeExtension($tempFile.FullName, '.xml')
    $tempProfile = Rename-Item -LiteralPath $tempFile.FullName -NewName ([IO.Path]::GetFileName($tempProfilePath)) -PassThru
    if ($SimIccId) {
        $xml = @"
<?xml version="1.0" encoding="utf-8"?>
<MBNProfileExt xmlns="http://www.microsoft.com/networking/WWAN/profile/v4">
  <Name>$profileName</Name>
  <IsDefault>false</IsDefault>
  <ProfileCreationType>UserProvisioned</ProfileCreationType>
  <SubscriberID>$SubscriberId</SubscriberID>
  <SimIccID>$SimIccId</SimIccID>
  <ConnectionMode>manual</ConnectionMode>
  <Context>
    <AccessString>ims</AccessString>
    <AuthProtocol>NONE</AuthProtocol>
    <IPType>IPv6</IPType>
  </Context>
</MBNProfileExt>
"@
    }
    else {
        $xml = @"
<?xml version="1.0" encoding="utf-8"?>
<MBNProfile xmlns="http://www.microsoft.com/networking/WWAN/profile/v1">
  <Name>$profileName</Name>
  <IsDefault>false</IsDefault>
  <ProfileCreationType>UserProvisioned</ProfileCreationType>
  <SubscriberID>$SubscriberId</SubscriberID>
  <ConnectionMode>manual</ConnectionMode>
  <Context>
    <AccessString>ims</AccessString>
    <AuthProtocol>NONE</AuthProtocol>
  </Context>
</MBNProfile>
"@
    }
    [IO.File]::WriteAllText($tempProfile.FullName, $xml, [Text.UTF8Encoding]::new($false))
    $SubscriberId = $null
    $SimIccId = $null
    $xml = $null

    $connectionAttempted = $true
    $connectStarted = Get-Date
    $connectResult = & netsh mbn connect "interface=$InterfaceName" 'connmode=tmp' "name=$($tempProfile.FullName)" 2>&1
    $connectResult | Out-Host
    if (($connectResult -join "`n") -match '0x139f') {
        Write-Output 'WWAN reported transient invalid state; waiting 20 seconds and retrying once.'
        Start-Sleep -Seconds 20
        $connectResult = & netsh mbn connect "interface=$InterfaceName" 'connmode=tmp' "name=$($tempProfile.FullName)" 2>&1
        $connectResult | Out-Host
    }
    $after = @()
    for ($sample = 1; $sample -le 18; $sample++) {
        Start-Sleep -Seconds 5
        $after = & netsh mbn show connection "interface=$InterfaceName" 2>&1
        $nonLinkLocal = @(Get-NetIPAddress -InterfaceAlias $InterfaceName -AddressFamily IPv6 -ErrorAction SilentlyContinue |
            Where-Object { $_.IPAddress -notlike 'fe80:*' })
        $activatedEvents = @(Get-WinEvent -FilterHashtable @{
            LogName = 'Microsoft-Windows-WWAN-SVC-Events/Operational'
            StartTime = $connectStarted
            Id = 11009
        } -ErrorAction SilentlyContinue | Where-Object {
            $_.Message -match 'WwanActivationStateActivated'
        })
        Write-Output "IMS observation $sample/18: connected=$(($after -join "`n") -match 'Interface State\s*:\s*Connected'), IPv6 addresses=$($nonLinkLocal.Count), activated events=$($activatedEvents.Count)"
        if (($after -join "`n") -match 'Interface State\s*:\s*Connected' -and
            $nonLinkLocal.Count -gt 0) { break }
    }
    $after | Out-Host
    Get-NetAdapter | Where-Object InterfaceAlias -eq $InterfaceName |
        Select-Object Name, InterfaceDescription, Status, InterfaceIndex | Format-Table -AutoSize
    Get-NetIPAddress | Where-Object {
        $_.InterfaceAlias -eq $InterfaceName -and $_.AddressFamily -eq 'IPv6'
    } | Select-Object InterfaceAlias, IPAddress, PrefixLength, AddressState | Format-Table -AutoSize
    if (($after -join "`n") -notmatch 'Interface State\s*:\s*Connected') {
        throw 'Windows did not activate the temporary primary IMS PDP context.'
    }
}
finally {
    if ($connectionAttempted) {
        Write-Output 'Disconnecting any primary PDP context opened by this probe.'
        & netsh mbn disconnect "interface=$InterfaceName"
        & netsh mbn show connection "interface=$InterfaceName"
    }
    if ($null -ne $tempProfile) {
        Remove-Item -LiteralPath $tempProfile.FullName -Force
    }
}
