# Strip 'Ethernet 9' to a bare IPv4 adapter to test whether an NDIS filter
# is what keeps software URO from ever coalescing. Run ELEVATED.
#
#   .\uro-strip-adapter.ps1            # save state, disable everything but IPv4, reset adapter
#   .\uro-strip-adapter.ps1 -Restore   # put every previously-enabled binding back
#
# ms_tcpip (IPv4) is never touched: the probe needs it. Everything else,
# including the WSL mirrored-networking bridge and the VMware/HTC/Npcap
# filters, comes off. Restoring re-enables exactly what was on before.
param([switch]$Restore)

$ErrorActionPreference = 'Stop'
$adapter = 'Ethernet 9'
$keep = @('ms_tcpip')
$stateFile = Join-Path $PSScriptRoot 'uro-strip-state.json'

if ($Restore) {
    if (-not (Test-Path $stateFile)) { throw "no saved state at $stateFile" }
    $saved = Get-Content $stateFile | ConvertFrom-Json
    foreach ($id in $saved) {
        try {
            Enable-NetAdapterBinding -Name $adapter -ComponentID $id -ErrorAction Stop
            Write-Output "restored: $id"
        } catch {
            Write-Warning "could not restore $id : $($_.Exception.Message)"
        }
    }
    Restart-NetAdapter -Name $adapter
    Start-Sleep -Seconds 8
    Write-Output "--- bindings now enabled:"
    Get-NetAdapterBinding -Name $adapter | Where-Object Enabled | Format-Table ComponentID, DisplayName -AutoSize
    return
}

$enabled = (Get-NetAdapterBinding -Name $adapter | Where-Object Enabled).ComponentID
$enabled | ConvertTo-Json | Set-Content $stateFile
Write-Output "saved $($enabled.Count) enabled bindings to $stateFile"

foreach ($id in $enabled) {
    if ($keep -contains $id) { continue }
    try {
        Disable-NetAdapterBinding -Name $adapter -ComponentID $id -ErrorAction Stop
        Write-Output "disabled: $id"
    } catch {
        Write-Warning "could not disable $id : $($_.Exception.Message)"
    }
}

Restart-NetAdapter -Name $adapter
Start-Sleep -Seconds 8
Write-Output "--- bindings still enabled:"
Get-NetAdapterBinding -Name $adapter | Where-Object Enabled | Format-Table ComponentID, DisplayName -AutoSize
Write-Output "--- URO capability:"
if (Get-NetAdapterUro -Name $adapter -ErrorAction SilentlyContinue) {
    Get-NetAdapterUro -Name $adapter | Format-List
} else {
    Write-Output "Get-NetAdapterUro: still nothing"
}
Write-Output "--- link:"
Get-NetAdapter -Name $adapter | Format-Table Status, LinkSpeed -AutoSize
