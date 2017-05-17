# Invoke all tests via Pester
# TODO does Pester handle isolation?  If so, can we Invoke-Pester without invoking a new powershell process?
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
Invoke-Pester -Script .\main.ps1