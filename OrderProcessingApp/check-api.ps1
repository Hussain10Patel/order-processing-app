$conn = Get-NetTCPConnection -LocalPort 5070 -State Listen -ErrorAction SilentlyContinue

if ($null -eq $conn)
{
    Write-Host 'API is NOT listening on port 5070.'
    exit 1
}

$ownerPid = ($conn | Select-Object -First 1 -ExpandProperty OwningProcess)
$proc = Get-Process -Id $ownerPid -ErrorAction SilentlyContinue

if ($null -eq $proc)
{
    Write-Host 'Port 5070 is listening but process details are unavailable.'
    exit 0
}

Write-Host "API is listening on port 5070 (PID=$ownerPid, Name=$($proc.ProcessName))."
exit 0
