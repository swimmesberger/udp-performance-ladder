# Definitive URO diagnosis: collect a verbose TCPIP trace while real UDP
# traffic is delivered to an URO-opted-in socket, then look for the event
# msquic's troubleshooting guide names ("URO SCU received. SegCount = ...")
# and for any URO disable-mask reason. Run ELEVATED.
$ErrorActionPreference = 'Continue'
$etl = "$env:TEMP\uro-trace.etl"
$txt = "$env:TEMP\uro-trace.txt"
$probe = 'C:\Users\Simon\AppData\Local\Temp\claude\C--Users-Simon-RiderProjects-cv\bb1e9adb-70b5-44fb-bb70-54888c220f08\scratchpad\uroprobe2'

Remove-Item $etl, $txt -ErrorAction SilentlyContinue

Write-Output "=== starting TCPIP trace (verbose) ==="
netsh trace start capture=no report=no overwrite=yes traceFile=$etl `
    provider=Microsoft-Windows-TCPIP level=5 keywords=0xffffffffffffffff | Out-String | Write-Output

Write-Output "=== running probe + generator ==="
$job = Start-Job -ScriptBlock {
    param($d) Set-Location $d
    dotnet run -c Release --no-build -- wire 1200 3
} -ArgumentList $probe
Start-Sleep -Seconds 3
$body = @{ target = '192.168.178.143:5000'; size = 1200; rate = 60000
           sendDurationSeconds = 6; threads = 1; sinkPort = 6000; sinkThreads = 1 } | ConvertTo-Json
try { Invoke-RestMethod -Method Post -Uri 'http://simondatastore:5390/runs' -ContentType 'application/json' -Body $body | Out-Null }
catch { Write-Warning "generator POST failed: $($_.Exception.Message)" }
Wait-Job $job -Timeout 45 | Out-Null
Receive-Job $job
Remove-Job $job -Force

Write-Output "=== stopping trace (this takes a moment) ==="
netsh trace stop | Out-String | Write-Output

if (Test-Path $etl) {
    Write-Output "=== converting ==="
    netsh trace convert input=$etl output=$txt overwrite=yes | Out-String | Write-Output
}

if (Test-Path $txt) {
    $lines = Get-Content $txt
    Write-Output "=== trace lines: $($lines.Count) ==="
    Write-Output "--- URO / coalesc / RSC hits:"
    $hits = $lines | Select-String -Pattern 'URO|coalesc|SegCount|RSC' -CaseSensitive:$false
    if ($hits) { $hits | Select-Object -First 40 | ForEach-Object { $_.Line.Trim() } }
    else { Write-Output "(none)" }
} else {
    Write-Output "conversion produced no text file; raw ETL at $etl"
}
