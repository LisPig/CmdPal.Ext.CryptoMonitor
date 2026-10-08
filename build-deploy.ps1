# Deploy the Crypto Monitor Command Palette extension: publish -> manifest -> msix -> sign -> install
# Usage: powershell -ExecutionPolicy Bypass -File build-deploy.ps1
$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$proj = Join-Path $root "CmdPal.Ext.CryptoMonitor\CmdPal.Ext.CryptoMonitor.csproj"
$stage = Join-Path $root "stage"
$appDir = Join-Path $stage "app"
$msix = Join-Path $root "CmdPal.Ext.CryptoMonitor.msix"
$certSubject = "CN=Crypto Monitor Dev"

# Names this project has been published under. The first one is selected when
# several are installed, and legacy names are removed so the palette does not end
# up listing two copies of the extension.
$packageName = "LisPig.CmdPal.Ext.CryptoMonitor"
$legacyPackageNames = @("CryptoMonitor")
$processNames = @("CmdPal.Ext.CryptoMonitor", "CryptoMonitor")

function Stop-Extension {
    foreach ($name in $processNames) {
        Get-Process -Name $name -ErrorAction SilentlyContinue | Stop-Process -Force
    }
    Start-Sleep -Milliseconds 400
}

# Loose registration keeps the old exe running; stop it so publish can overwrite.
# (The host may relaunch it while we work, so this is repeated before the wipe.)
Stop-Extension

Write-Host "== 0/6 clean the previous publish output =="
# dotnet publish does not clean its -o target: without this the package keeps
# stale files from an older assembly name.
if (Test-Path $appDir) {
    Remove-Item $appDir -Recurse -Force -ErrorAction SilentlyContinue
    if (Test-Path $appDir) { Stop-Extension; Remove-Item $appDir -Recurse -Force }
}

Write-Host "== 1/6 dotnet publish (win-x64, framework-dependent) =="
& dotnet publish $proj -c Debug -p:Platform=x64 -r win-x64 --self-contained false -o $appDir | Out-Host
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

Write-Host "== 2/6 copy assets + build AppxManifest.xml =="
$assetSrc = Join-Path $root "CmdPal.Ext.CryptoMonitor\Assets"
if (Test-Path $assetSrc) {
    New-Item -ItemType Directory -Force (Join-Path $appDir "Assets") | Out-Null
    Copy-Item (Join-Path $assetSrc '*') (Join-Path $appDir "Assets") -Recurse -Force
}

$manifestTemplate = Join-Path $root "CmdPal.Ext.CryptoMonitor\Package.appxmanifest"
$manifestOut = Join-Path $appDir "AppxManifest.xml"
$xml = Get-Content $manifestTemplate -Raw
$xml = $xml -replace '\$targetnametoken\$', 'CmdPal.Ext.CryptoMonitor'
$xml = $xml -replace '\$targetentrypoint\$', 'Windows.FullTrustApplication'
# Publisher must match the signing certificate subject (this is the local dev cert;
# a Store submission keeps its Partner Center publisher instead)
$xml = $xml -replace 'Publisher="CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US"', "Publisher=`"$certSubject`""
# remove the store-facing DisplayName placeholders that confuse local dev
$xml = $xml -replace 'TemplateDisplayName', 'Crypto Monitor'
# loose registration cannot auto-generate PRI; pin an explicit language instead
$xml = $xml -replace '<Resource Language="x-generate"/>', '<Resource Language="en-US"/>'
Set-Content $manifestOut $xml -Encoding UTF8
Write-Host "manifest -> $manifestOut"

Write-Host "== 3/6 signing certificate =="
$cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $certSubject } | Select-Object -First 1
if (-not $cert) {
    # Self-signed cert must be usable as a root CA (CA=1) or the chain is rejected.
    $cert = New-SelfSignedCertificate -Type CodeSigningCert `
        -Subject $certSubject `
        -CertStoreLocation Cert:\CurrentUser\My `
        -NotAfter (Get-Date).AddYears(3) `
        -TextExtension @('2.5.29.19={critical}{text}CA=1')
    Write-Host "created cert thumbprint $($cert.Thumbprint)"
}
# trust it for sideloading (root must be the CA-enabled cert above)
$rootCert = Get-ChildItem Cert:\CurrentUser\Root | Where-Object { $_.Thumbprint -eq $cert.Thumbprint }
if (-not $rootCert) {
    Import-Certificate -FilePath (Export-Certificate -Cert $cert -FilePath (Join-Path $stage "dev.cer")) -CertStoreLocation Cert:\CurrentUser\Root | Out-Null
    Write-Host "imported root cert"
}
$pfx = Join-Path $stage "dev.pfx"
$pfxPass = "crypto-dev"
Export-PfxCertificate -Cert $cert -FilePath $pfx -Password (ConvertTo-SecureString $pfxPass -AsPlainText -Force) | Out-Null

Write-Host "== 4/6 makeappx pack =="
$kit = Get-ChildItem "C:\Program Files (x86)\Windows Kits\10\bin" -Directory | Where-Object { $_.Name -like "10.0.*" } | Sort-Object Name -Descending | Select-Object -First 1
$makeappx = Join-Path $kit.FullName "x64\makeappx.exe"
$signtool = Join-Path $kit.FullName "x64\signtool.exe"
if (-not (Test-Path $makeappx)) { throw "makeappx not found under $($kit.FullName)" }
& $makeappx pack /d $appDir /p $msix /o | Out-Host
if ($LASTEXITCODE -ne 0) { throw "makeappx failed" }

Write-Host "== 5/6 sign =="
& $signtool sign /f $pfx /p $pfxPass /fd SHA256 $msix | Out-Host
if ($LASTEXITCODE -ne 0) { throw "signtool failed" }

Write-Host "== 6/6 install (loose register, needs Developer Mode) =="
Stop-Extension
foreach ($name in (@($packageName) + $legacyPackageNames)) {
    $existing = Get-AppxPackage -Name $name -ErrorAction SilentlyContinue
    if ($existing) {
        Write-Host "removing existing package $($existing.PackageFullName)"
        Remove-AppxPackage $existing.PackageFullName -ErrorAction SilentlyContinue
    }
}
Add-AppxPackage -Register $manifestOut -ForceApplicationShutdown
$installed = Get-AppxPackage -Name $packageName
if ($installed) { Write-Host "OK installed: $($installed.PackageFullName)  status=$($installed.Status)" } else { throw "install failed" }
Write-Host "Done. Run 'Reload' in Command Palette (or restart PowerToys)."
