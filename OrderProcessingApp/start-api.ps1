$ErrorActionPreference = 'Stop'

Write-Host 'Stopping old OrderProcessingApp processes (if any)...'
Get-Process -Name 'OrderProcessingApp' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

Write-Host 'Starting API on http://localhost:5070 ...'
dotnet run
