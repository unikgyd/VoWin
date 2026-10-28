param(
    [Parameter(Mandatory = $true)] [string[]] $PcscfAddresses,
    [ValidateRange(0, 60)] [int] $HoldSeconds = 0,
    [string] $CliExecutable,
    [string] $NpcapProbeExecutable,
    [string] $WinDivertNetdumpExecutable,
    [string] $WinDivertManagedProbeDll,
    [string] $AtPort = 'COM6',
    [switch] $SimulateOuterCrash,
    [switch] $WaitForStop,
    [System.Diagnostics.Process] $PppLeaseProcess,
    [ValidateRange(15, 3600)] [int] $MaxLeaseSeconds = 3600
)

# Diagnostic only: briefly replace this RAS entry's IPv6 default route with
# exact P-CSCF host routes, inspect the table, then restore its original route.
# Never persist routes or touch another interface. The owning PPP probe must
# remain connected for the entire run.
$ErrorActionPreference = 'Stop'
$alias = 'VoWin IMS PPP Probe'
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'An elevated Windows administrator token is required.'
}
if ($SimulateOuterCrash -and $env:VOWIN_IMS_ALLOW_CRASH_PROBE -ne '1') {
    throw 'Deliberate owner-crash tests require VOWIN_IMS_ALLOW_CRASH_PROBE=1.'
}
if ($WaitForStop -and -not [Console]::IsInputRedirected) {
    throw 'A managed IMS bearer requires redirected parent stdin for STOP/EOF.'
}
if ($WaitForStop -and ($null -eq $PppLeaseProcess -or
    -not $PppLeaseProcess.StartInfo.RedirectStandardInput)) {
    throw 'A managed IMS bearer requires its controlled RAS process for lease renewal.'
}

$interfaces = @(Get-NetIPInterface -AddressFamily IPv6 -InterfaceAlias $alias -ErrorAction SilentlyContinue)
if ($interfaces.Count -ne 1 -or $interfaces[0].ConnectionState -ne 'Connected') {
    throw 'The controlled IMS PPP interface is not uniquely connected.'
}
$index = [uint32] $interfaces[0].InterfaceIndex
$locals = @(Get-NetIPAddress -InterfaceIndex $index -AddressFamily IPv6 |
    Where-Object { $_.AddressState -eq 'Preferred' -and $_.IPAddress -notmatch '^fe80:' })
if ($locals.Count -ne 1) { throw 'Expected exactly one preferred global IMS PPP address.' }
$localIp = [System.Net.IPAddress]::Parse($locals[0].IPAddress)
if ($localIp.IsIPv6LinkLocal -or $localIp.IsIPv6Multicast -or
    [System.Net.IPAddress]::IsLoopback($localIp)) {
    throw 'PPP address is not a usable IMS IPv6 source.'
}

$pcscf = @($PcscfAddresses | ForEach-Object {
    $parsed = [System.Net.IPAddress]::Parse($_)
    if ($parsed.AddressFamily -ne [System.Net.Sockets.AddressFamily]::InterNetworkV6 -or
        $parsed.IsIPv6LinkLocal -or $parsed.IsIPv6Multicast -or
        [System.Net.IPAddress]::IsLoopback($parsed) -or $parsed.Equals($localIp)) {
        throw "Invalid P-CSCF IPv6 address: $_"
    }
    $parsed.ToString()
} | Select-Object -Unique)
if ($pcscf.Count -lt 1 -or $pcscf.Count -gt 4) {
    throw 'Provide one to four distinct IPv6 P-CSCF addresses.'
}

$defaults = @(Get-NetRoute -AddressFamily IPv6 -InterfaceIndex $index -DestinationPrefix '::/0')
if ($defaults.Count -ne 1 -or $defaults[0].NextHop -notmatch '^fe80:') {
    throw 'Expected one link-local PPP default route; no route was changed.'
}
$original = $defaults[0]
$nextHop = [string] $original.NextHop
$metric = [uint16] $original.RouteMetric
$added = [System.Collections.Generic.List[string]]::new()
$mediaRoutes = [System.Collections.Generic.List[string]]::new()
$defaultRemoved = $false
$restoreError = $null
function Assert-ImsHostRoute([string] $address) {
    $selected = @(Find-NetRoute -RemoteIPAddress $address -ErrorAction Stop)
    if ($selected.Count -ne 2 -or $selected[0].InterfaceIndex -ne $index -or
        $selected[1].InterfaceIndex -ne $index -or
        $selected[0].IPAddress -ne $localIp.ToString() -or
        $selected[1].DestinationPrefix -ne "$address/128") {
        throw "$address does not select the exact IMS PPP source and host route."
    }
}
try {
    foreach ($address in $pcscf) {
        $prefix = "$address/128"
        if (@(Get-NetRoute -AddressFamily IPv6 -InterfaceIndex $index -DestinationPrefix $prefix -ErrorAction SilentlyContinue).Count -ne 0) {
            throw "An exact P-CSCF route already exists: $prefix"
        }
        New-NetRoute -DestinationPrefix $prefix -InterfaceIndex $index -NextHop $nextHop -RouteMetric 1 -PolicyStore ActiveStore -ErrorAction Stop | Out-Null
        $added.Add($prefix)
    }
    Remove-NetRoute -InputObject $original -Confirm:$false -ErrorAction Stop
    $defaultRemoved = $true
    if (@(Get-NetRoute -AddressFamily IPv6 -InterfaceIndex $index -DestinationPrefix '::/0' -ErrorAction SilentlyContinue).Count -ne 0) {
        throw 'PPP default route remains after removal.'
    }
    foreach ($address in $pcscf) {
        Assert-ImsHostRoute $address
        Write-Output "Isolated P-CSCF $address via PPP interface $index and $localIp."
    }
    Write-Output 'No IPv6 default route remains on the IMS PPP interface during isolation.'
    if ($NpcapProbeExecutable) {
        if (-not (Test-Path -LiteralPath $NpcapProbeExecutable -PathType Leaf)) {
            throw "Npcap interface probe not found: $NpcapProbeExecutable"
        }
        & $NpcapProbeExecutable $index
        if ($LASTEXITCODE -ne 0) {
            throw 'Npcap did not expose a DLT_RAW capture handle for the exact IMS PPP interface.'
        }
        Write-Output 'Npcap can open the exact Windows IMS PPP interface as DLT_RAW.'
    }
    if ($WinDivertNetdumpExecutable) {
        & (Join-Path $PSScriptRoot 'Probe-WinDivertOnImsPpp.ps1') `
            -InterfaceIndex $index -LocalAddress $localIp.ToString() `
            -PcscfAddress $pcscf[0] -NetdumpExecutable $WinDivertNetdumpExecutable
    }
    if ($WinDivertManagedProbeDll) {
        & (Join-Path $PSScriptRoot 'Probe-ManagedWinDivertOnImsPpp.ps1') `
            -InterfaceIndex $index -LocalAddress $localIp.ToString() `
            -PcscfAddress $pcscf[0] -WinDivertDll $WinDivertManagedProbeDll
    }
    if ($CliExecutable) {
        if (-not (Test-Path -LiteralPath $CliExecutable -PathType Leaf)) {
            throw "VoSharp CLI executable not found: $CliExecutable"
        }
        $commands = @("attach $AtPort", 'host-ims probe', 'quit')
        $cliOutput = $commands | & $CliExecutable 2>&1
        $cliText = $cliOutput -join "`n"
        $cliOutput | Where-Object { $_ -match 'Host IMS:|CanAttemptWindowsIms|HostRoutable|\[Error\]|Connection error' } | Write-Output
        if ($LASTEXITCODE -ne 0 -or
            $cliText -notmatch 'Host IMS: HostRoutable' -or
            $cliText -notmatch '"CanAttemptWindowsIms": true') {
            throw 'VoSharp did not confirm a HostRoutable IMS endpoint during route isolation.'
        }
        Write-Output 'VoSharp confirmed HostRoutable during isolated PPP routing.'
    }
    if ($SimulateOuterCrash) {
        [Environment]::Exit(93) # Lab-only: bypass both route and modem finally.
    }
    if ($WaitForStop) {
        $ready = [pscustomobject]@{
            InterfaceIndex = $index
            LocalAddress = $localIp.ToString()
            PcscfAddresses = $pcscf
        } | ConvertTo-Json -Compress -Depth 3
        Write-Output "VOWIN_IMS_BEARER_READY $ready"
        $stopInput = [Console]::In.ReadLineAsync()
        $deadline = [DateTime]::UtcNow.AddSeconds($MaxLeaseSeconds)
        while ([DateTime]::UtcNow -lt $deadline) {
            if ($stopInput.IsCompleted) {
                $line = $stopInput.GetAwaiter().GetResult()
                if ($null -eq $line -or $line -eq 'STOP') {
                    Write-Output 'IMS bearer owner requested stop or closed stdin.'
                    break
                }
                if ($line -eq 'RENEW') {
                    Write-Output 'VOWIN_IMS_LEASE_RENEW_REQUEST'
                    if ($PppLeaseProcess.HasExited) {
                        throw 'IMS PPP disconnected before lease renewal.'
                    }
                    # The loop below independently checks the live PPP and all
                    # exact routes every two seconds. Repeating CIM route queries
                    # here delayed a field heartbeat past its ACK deadline.
                    $PppLeaseProcess.StandardInput.WriteLine('RENEW')
                    $PppLeaseProcess.StandardInput.Flush()
                    Write-Output 'VOWIN_IMS_LEASE_RAS_RENEW_SENT'
                    $deadline = [DateTime]::UtcNow.AddSeconds($MaxLeaseSeconds)
                    Write-Output 'VOWIN_IMS_LEASE_RENEWED'
                    $stopInput = [Console]::In.ReadLineAsync()
                    continue
                }
                if ($line -match '^ROUTE ADD ([0-9A-Fa-f:]+)$') {
                    $live = @(Get-NetIPInterface -AddressFamily IPv6 -InterfaceIndex $index -ErrorAction SilentlyContinue |
                        Where-Object { $_.ConnectionState -eq 'Connected' })
                    $leakedDefault = @(Get-NetRoute -AddressFamily IPv6 -InterfaceIndex $index -DestinationPrefix '::/0' -ErrorAction SilentlyContinue)
                    if ($live.Count -ne 1 -or $leakedDefault.Count -ne 0) {
                        Write-Output "VOWIN_IMS_ROUTE_GUARD_FAILURE live=$($live.Count) default=$($leakedDefault.Count)"
                        throw 'IMS PPP disconnected or lost default-route isolation before media routing.'
                    }
                    $requestedAddress = $Matches[1]
                    $routeCreated = $false
                    $prefix = $null
                    try {
                        $mediaIp = [System.Net.IPAddress]::Parse($requestedAddress)
                        if ($mediaIp.AddressFamily -ne [System.Net.Sockets.AddressFamily]::InterNetworkV6 -or
                            $mediaIp.IsIPv4MappedToIPv6 -or $mediaIp.IsIPv6LinkLocal -or
                            $mediaIp.IsIPv6Multicast -or [System.Net.IPAddress]::IsLoopback($mediaIp) -or
                            $mediaIp.Equals([System.Net.IPAddress]::IPv6Any) -or $mediaIp.Equals($localIp)) {
                            throw 'IMS media route command supplied an unusable IPv6 destination.'
                        }
                        $address = $mediaIp.ToString()
                        if (-not $pcscf.Contains($address) -and -not $mediaRoutes.Contains($address)) {
                            if ($mediaRoutes.Count -ge 8) { throw 'IMS media route limit of eight destinations was reached.' }
                            $prefix = "$address/128"
                            if (@(Get-NetRoute -AddressFamily IPv6 -InterfaceIndex $index -DestinationPrefix $prefix -ErrorAction SilentlyContinue).Count -ne 0) {
                                throw 'An exact IMS media route already exists.'
                            }
                            New-NetRoute -DestinationPrefix $prefix -InterfaceIndex $index -NextHop $nextHop -RouteMetric 1 -PolicyStore ActiveStore -ErrorAction Stop | Out-Null
                            $routeCreated = $true
                        }
                        Assert-ImsHostRoute $address
                        if ($routeCreated) {
                            $added.Add($prefix)
                            $mediaRoutes.Add($address)
                        }
                        Write-Output "VOWIN_IMS_MEDIA_ROUTE_ADDED $address"
                    }
                    catch {
                        # A rejected SDP route is not a reason to drop the SIP
                        # bearer. If cleanup or isolation fails, the outer
                        # finally still tears down PPP and restores the modem.
                        if ($routeCreated) {
                            Get-NetRoute -AddressFamily IPv6 -InterfaceIndex $index -DestinationPrefix $prefix -ErrorAction SilentlyContinue |
                                Remove-NetRoute -Confirm:$false -ErrorAction Stop
                            if (@(Get-NetRoute -AddressFamily IPv6 -InterfaceIndex $index -DestinationPrefix $prefix -ErrorAction SilentlyContinue).Count -ne 0) {
                                throw 'Rejected IMS media route could not be removed.'
                            }
                        }
                        Write-Output 'VOWIN_IMS_MEDIA_ROUTE_REJECTED'
                    }
                    $stopInput = [Console]::In.ReadLineAsync()
                    continue
                }
                if ($line -match '^ROUTE REMOVE ([0-9A-Fa-f:]+)$') {
                    $address = ([System.Net.IPAddress]::Parse($Matches[1])).ToString()
                    if ($pcscf.Contains($address) -or -not $mediaRoutes.Contains($address)) {
                        throw 'IMS route removal requested a destination not owned as media.'
                    }
                    $prefix = "$address/128"
                    $ownedRoute = @(Get-NetRoute -AddressFamily IPv6 -InterfaceIndex $index -DestinationPrefix $prefix -ErrorAction Stop)
                    if ($ownedRoute.Count -ne 1 -or [string]$ownedRoute[0].NextHop -ne $nextHop) {
                        throw 'IMS media route changed before removal.'
                    }
                    $ownedRoute | Remove-NetRoute -Confirm:$false -ErrorAction Stop
                    if (@(Get-NetRoute -AddressFamily IPv6 -InterfaceIndex $index -DestinationPrefix $prefix -ErrorAction SilentlyContinue).Count -ne 0) {
                        throw 'IMS media route remained after removal.'
                    }
                    [void]$mediaRoutes.Remove($address)
                    [void]$added.Remove($prefix)
                    Write-Output "VOWIN_IMS_MEDIA_ROUTE_REMOVED $address"
                    $stopInput = [Console]::In.ReadLineAsync()
                    continue
                }
                throw 'IMS bearer control pipe received an unexpected command.'
            }
            $live = @(Get-NetIPInterface -AddressFamily IPv6 -InterfaceIndex $index -ErrorAction SilentlyContinue |
                Where-Object { $_.ConnectionState -eq 'Connected' })
            $leakedDefault = @(Get-NetRoute -AddressFamily IPv6 -InterfaceIndex $index -DestinationPrefix '::/0' -ErrorAction SilentlyContinue)
            if ($live.Count -ne 1 -or $leakedDefault.Count -ne 0) {
                Write-Output "VOWIN_IMS_ROUTE_GUARD_FAILURE live=$($live.Count) default=$($leakedDefault.Count)"
                throw 'IMS PPP was disconnected or its isolated route policy changed.'
            }
            foreach ($address in $pcscf) {
                if (@(Get-NetRoute -AddressFamily IPv6 -InterfaceIndex $index -DestinationPrefix "$address/128" -ErrorAction SilentlyContinue).Count -ne 1) {
                    throw "P-CSCF host route disappeared during the IMS bearer lease: $address"
                }
            }
            foreach ($address in $mediaRoutes) {
                if (@(Get-NetRoute -AddressFamily IPv6 -InterfaceIndex $index -DestinationPrefix "$address/128" -ErrorAction SilentlyContinue).Count -ne 1) {
                    throw "Media host route disappeared during the IMS bearer lease: $address"
                }
            }
            Start-Sleep -Seconds 2
        }
        if (-not $stopInput.IsCompleted) { throw 'IMS bearer reached its maximum lease time.' }
    }
    if ($HoldSeconds -gt 0) {
        Write-Output "Holding isolated routes for $HoldSeconds seconds for read-only application probes."
        Start-Sleep -Seconds $HoldSeconds
    }
}
catch {
    # Tell the owning CLI immediately that the protected bearer is no longer
    # usable. Its AT handle must close before the parent restores modem IMS.
    Write-Output 'VOWIN_IMS_BEARER_LOST'
    Write-Output "IMS route isolation failed: $($_.Exception.GetType().Name): $($_.Exception.Message)"
    throw
}
finally {
    # Restore the route before removing temporary host routes. If PPP already
    # disconnected, its OS-owned default and our host routes disappear with it.
    if ($defaultRemoved -and @(Get-NetIPInterface -AddressFamily IPv6 -InterfaceIndex $index -ErrorAction SilentlyContinue |
            Where-Object { $_.ConnectionState -eq 'Connected' }).Count -eq 1) {
        try {
            $existingDefault = @(Get-NetRoute -AddressFamily IPv6 -InterfaceIndex $index -DestinationPrefix '::/0' -ErrorAction SilentlyContinue)
            if ($existingDefault.Count -eq 0) {
                New-NetRoute -DestinationPrefix '::/0' -InterfaceIndex $index -NextHop $nextHop -RouteMetric $metric -PolicyStore ActiveStore -ErrorAction Stop | Out-Null
            }
            elseif ($existingDefault.Count -ne 1 -or $existingDefault[0].NextHop -ne $nextHop) {
                throw 'An unexpected PPP default route appeared during restore.'
            }
        }
        catch { $restoreError = $_ }
    }
    foreach ($prefix in $added) {
        try {
            Get-NetRoute -AddressFamily IPv6 -InterfaceIndex $index -DestinationPrefix $prefix -ErrorAction SilentlyContinue |
                Remove-NetRoute -Confirm:$false -ErrorAction Stop
        }
        catch { if ($null -eq $restoreError) { $restoreError = $_ } }
    }
    if ($null -ne $restoreError) { throw "PPP route restoration failed: $restoreError" }
}
