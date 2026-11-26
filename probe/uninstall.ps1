#Requires -RunAsAdministrator
# Removes the throwaway sensor probe service and its program folder. Keeps the logs in
# C:\ProgramData\pcmon-probe.

$name = 'pcmon-probe'
sc.exe stop $name | Out-Null
Start-Sleep -Seconds 3
sc.exe delete $name
Remove-Item -Recurse -Force -ErrorAction SilentlyContinue (Join-Path $env:ProgramFiles 'pcmon-probe')
Write-Host 'Removed. Logs kept in C:\ProgramData\pcmon-probe.'
