<#
.SYNOPSIS
    Diagnose and (temporarily) clear whatever is stopping software UDP
    Receive Offload (URO) from coalescing on Windows, with an exact restore.

.DESCRIPTION
    Software URO can be switched off by two independent classes of thing,
    neither of which any user-space API reports:

      1. NDIS filter drivers bound to the interface. A lightweight filter
         targeting an older NDIS version makes the stack disable coalescing
         (see nmap/npcap#737 for the canonical example).
      2. A GLOBAL disable mask, visible only in a TCPIP trace:
              UDP software URO global disabled mask = N
         where 0 = healthy, 2 = an incompatible WFP callout is registered,
         and 48 = incompatible IPSNPI clients, namely winnat or FSE, which
         are enabled automatically when WSL or Hyper-V are enabled.

    -Diagnose reports everything cheap. -Apply records the current state and
    then removes both classes of blocker it can remove without a reboot.
    -Restore puts every recorded setting back exactly.

    Read the mask itself with uro-trace.ps1 (elevated) before and after.

.EXAMPLE
    .\fix-uro.ps1 -Diagnose
    .\fix-uro.ps1 -Apply      # elevated
    .\fix-uro.ps1 -Restore    # elevated
#>
[CmdletBinding(DefaultParameterSetName = 'Diagnose')]
param(
    [Parameter(ParameterSetName = 'Diagnose')][switch]$Diagnose,
    [Parameter(ParameterSetName = 'Apply')][switch]$Apply,
    [Parameter(ParameterSetName = 'Restore')][switch]$Restore,
    [string]$Adapter = 'Ethernet 9',
    [switch]$SkipFilters,
    [switch]$SkipIpsnpi
)

$ErrorActionPreference = 'Continue'
$stateFile  = Join-Path $PSScriptRoot 'uro-state.json'
$legacyFile = Join-Path $PSScriptRoot 'uro-strip-state.json'

# Bindings never touched: the probe needs IPv4.
$keepAlways = @('ms_tcpip')

# NDIS filters/protocols known or suspected to suppress coalescing. Each is
# restored verbatim afterwards; the comment is why it is on the list.
$suspectFilters = [ordered]@{
    'INSECURE_NPCAP' = 'Npcap capture driver (NDIS version compat, nmap/npcap#737)'
    'vmware_bridge'  = 'VMware Bridge Protocol'
    'MS_NDISPROT'    = 'vendor NDIS protocol driver'
    'ms_l2bridge'    = 'layer-2 bridge (WSL mirrored networking)'
    'ms_l1vhlwf'     = 'nested network virtualization LWF'
    'ms_pacer'       = 'QoS Packet Scheduler'
}

# IPSNPI-client surface: Hyper-V NAT/container virtual adapters + winnat.
$vnicPattern = 'FSE HostVnic|Default Switch|WSL'

function Test-Elevated {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    (New-Object Security.Principal.WindowsPrincipal $id).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Show-State {
    Write-Output "=== adapter: $Adapter ==="
    Get-NetAdapter -Name $Adapter -ErrorAction SilentlyContinue |
        Format-Table Name, Status, LinkSpeed -AutoSize
    Write-Output "--- enabled bindings:"
    Get-NetAdapterBinding -Name $Adapter -ErrorAction SilentlyContinue |
        Where-Object Enabled | Format-Table ComponentID, DisplayName -AutoSize
    Write-Output "--- IPSNPI clients (mask 48 suspects):"
    Get-Service winnat -ErrorAction SilentlyContinue | Format-Table Name, Status -AutoSize
    Get-NetAdapter | Where-Object { $_.Name -match $vnicPattern } |
        Format-Table Name, Status -AutoSize
    Write-Output "--- hardware URO (a DIFFERENT question from software URO):"
    if (Get-NetAdapterUro -Name $Adapter -ErrorAction SilentlyContinue) {
        Get-NetAdapterUro -Name $Adapter | Format-List
    } else { Write-Output "Get-NetAdapterUro: none (says nothing about software URO)" }
    Write-Output "--- global receive-offload switch:"
    netsh int udp show global
    Write-Output "--- the mask itself: run uro-trace.ps1 (elevated) and grep"
    Write-Output "    'UDP software URO global disabled mask'  (0 = healthy)"
}

if ($Restore) {
    if (-not (Test-Elevated)) { throw 'run elevated' }
    if (-not (Test-Path $stateFile)) { throw "no saved state at $stateFile" }
    $s = Get-Content $stateFile | ConvertFrom-Json

    foreach ($id in $s.Bindings) {
        try { Enable-NetAdapterBinding -Name $s.Adapter -ComponentID $id -ErrorAction Stop
              Write-Output "restored binding: $id" }
        catch { Write-Warning "binding $id : $($_.Exception.Message)" }
    }
    foreach ($n in $s.DisabledAdapters) {
        try { Enable-NetAdapter -Name $n -Confirm:$false -ErrorAction Stop
              Write-Output "restored adapter: $n" }
        catch { Write-Warning "adapter $n : $($_.Exception.Message)" }
    }
    if ($s.WinNatWasRunning) {
        try { Start-Service winnat -ErrorAction Stop; Write-Output 'restored: winnat started' }
        catch { Write-Warning "winnat: $($_.Exception.Message)" }
    }
    Restart-NetAdapter -Name $s.Adapter -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 8
    Remove-Item $stateFile -ErrorAction SilentlyContinue
    Remove-Item $legacyFile -ErrorAction SilentlyContinue
    Write-Output ''
    Show-State
    return
}

if ($Apply) {
    if (-not (Test-Elevated)) { throw 'run elevated' }

    # The bindings to restore later are the ones enabled BEFORE any of this
    # started: prefer a legacy strip-state file if a previous run left the
    # adapter already stripped, otherwise the current set.
    $current = @((Get-NetAdapterBinding -Name $Adapter | Where-Object Enabled).ComponentID)
    $original = $current
    if (Test-Path $legacyFile) {
        $legacy = @(Get-Content $legacyFile | ConvertFrom-Json)
        if ($legacy.Count -gt $current.Count) {
            $original = $legacy
            Write-Output "absorbed earlier strip state ($($legacy.Count) bindings)"
        }
    }

    $vnics = @()
    if (-not $SkipIpsnpi) {
        $vnics = @((Get-NetAdapter | Where-Object {
            $_.Name -match $vnicPattern -and $_.Status -ne 'Disabled' }).Name)
    }

    [pscustomobject]@{
        Adapter          = $Adapter
        Bindings         = $original
        DisabledAdapters = $vnics
        WinNatWasRunning = ((Get-Service winnat -ErrorAction SilentlyContinue).Status -eq 'Running')
        SavedUtc         = (Get-Date).ToUniversalTime().ToString('o')
    } | ConvertTo-Json | Set-Content $stateFile
    Write-Output "state saved to $stateFile"

    if (-not $SkipFilters) {
        foreach ($id in $suspectFilters.Keys) {
            $b = Get-NetAdapterBinding -Name $Adapter -ComponentID $id -ErrorAction SilentlyContinue
            if ($b -and $b.Enabled -and $keepAlways -notcontains $id) {
                try { Disable-NetAdapterBinding -Name $Adapter -ComponentID $id -ErrorAction Stop
                      Write-Output "disabled filter: $id  ($($suspectFilters[$id]))" }
                catch { Write-Warning "filter $id : $($_.Exception.Message)" }
            }
        }
    }

    if (-not $SkipIpsnpi) {
        foreach ($n in $vnics) {
            try { Disable-NetAdapter -Name $n -Confirm:$false -ErrorAction Stop
                  Write-Output "disabled adapter: $n" }
            catch { Write-Warning "adapter $n : $($_.Exception.Message)" }
        }
        try { Stop-Service winnat -Force -ErrorAction Stop; Write-Output 'stopped: winnat' }
        catch { Write-Warning "winnat: $($_.Exception.Message)" }
    }

    Start-Sleep -Seconds 5
    Write-Output ''
    Show-State
    Write-Output ''
    Write-Output 'Next: uro-trace.ps1 (elevated). Mask 0 => URO can coalesce.'
    Write-Output 'Still 48 => the IPSNPI clients are registered at boot; the only'
    Write-Output 'known clearing move is disabling Hyper-V/WSL features + reboot.'
    return
}

Show-State
