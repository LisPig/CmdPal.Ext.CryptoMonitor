$ErrorActionPreference = "Stop"
# 1) Enable Developer Mode (allows sideloading dev packages)
$key = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock"
if (-not (Test-Path $key)) { New-Item -Path $key -Force | Out-Null }
Set-ItemProperty -Path $key -Name "AllowDevelopmentWithoutDevLicense" -Value 1 -Type DWord
Write-Host "developer mode enabled"
# 2) Trust the dev cert at machine level too
Import-Certificate -FilePath (Join-Path $PSScriptRoot "stage\dev.cer") -CertStoreLocation Cert:\LocalMachine\Root | Out-Null
Write-Host "machine root cert imported"
