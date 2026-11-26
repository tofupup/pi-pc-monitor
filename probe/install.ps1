#Requires -RunAsAdministrator
# Installs the throwaway sensor probe as an Automatic-start LocalSystem service that restarts
# after a crash. Run from an elevated PowerShell in the folder holding pcmon-probe.exe.
# If these commands work unchanged, they are the record for "How are the Reporter and the Kiosk
# installed and updated?".

$ErrorActionPreference = 'Stop'
$name = 'pcmon-probe'
$dir  = Join-Path $env:ProgramFiles 'pcmon-probe'
$exe  = Join-Path $dir 'pcmon-probe.exe'

function Check([string]$what) {
    if ($LASTEXITCODE -ne 0) { throw "$what failed with exit code $LASTEXITCODE" }
}

New-Item -ItemType Directory -Force -Path $dir | Out-Null
Copy-Item -Force (Join-Path $PSScriptRoot 'pcmon-probe.exe') $exe

# New-Service registers under LocalSystem by default and quotes the path correctly.
New-Service -Name $name `
    -BinaryPathName "`"$exe`"" `
    -StartupType Automatic `
    -DisplayName 'PC Monitor sensor probe (throwaway)' `
    -Description 'Throwaway probe for the PC Monitor project. Logs sensor readings to C:\ProgramData\pcmon-probe\probe.log.' |
    Out-Null

# Restart 5 s after each crash; reset the failure count after a day.
sc.exe failure $name reset= 86400 actions= restart/5000/restart/5000/restart/5000; Check 'sc.exe failure'
# Also count "stopped with an error" as a failure, not only a crash.
sc.exe failureflag $name 1; Check 'sc.exe failureflag'

sc.exe start $name; Check 'sc.exe start'

Write-Host ''
Write-Host "Installed and started. Watch it with:"
Write-Host "  Get-Content C:\ProgramData\pcmon-probe\probe.log -Wait -Tail 20"
