# Builds the Microsoft Store package for Overskin into dist\
#
#   ./src/Store/build-store.ps1              dist\Overskin_<version>_x64.msix, unsigned, for Partner Center
#   ./src/Store/build-store.ps1 -TestSign    also dist\Overskin_<version>_x64.test.msix, signed with a
#                                            self-signed certificate so it can be installed on this PC
#
# The GitHub installer (build.ps1, OverskinSetup.exe) is separate and unchanged.
#
# Steps: publish the app with STORE defined (self-contained, not single-file: the package is
# compressed already), lay it out with the manifest and images, then pack it with makeappx from
# the Windows SDK. The Store signs the package itself, so the main .msix is left unsigned.

param(
    [switch] $TestSign,
    [string] $Out = ""
)

$ErrorActionPreference = "Stop"
$here = $PSScriptRoot
$root = (Resolve-Path (Join-Path $here "..\..")).Path
$dist = if ($Out) { [IO.Path]::GetFullPath($Out) } else { Join-Path $root "dist" }
$work = Join-Path $root "build\store"
$app = Join-Path $work "app"
$layout = Join-Path $work "layout"

# ---- the Windows SDK tools --------------------------------------------------------------------
function Find-SdkTool([string] $name) {
    $kits = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin"
    $found = Get-ChildItem -Path $kits -Filter $name -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\x64\\' } |
        Sort-Object { [version]($_.FullName -replace '^.*\\bin\\([\d.]+)\\.*$', '$1') } -Descending |
        Select-Object -First 1
    if (-not $found) { throw "$name wasn't found. Install the Windows 10/11 SDK (it comes with Visual Studio's 'Windows application development' workload)." }
    return $found.FullName
}
$makeappx = Find-SdkTool "makeappx.exe"
$makepri = Find-SdkTool "makepri.exe"

# ---- 1. publish the app with STORE defined ----------------------------------------------------
Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $dist, $app, $layout | Out-Null

dotnet publish (Join-Path $root "src/Overskin/Overskin.csproj") -c Release -r win-x64 --self-contained true `
    -p:Store=true -p:PublishReadyToRun=true -o $app --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw "Overskin build failed" }
Remove-Item (Join-Path $app "*.pdb") -ErrorAction SilentlyContinue

# ---- 2. the layout: the app, the manifest and the images ----------------------------------------
Copy-Item (Join-Path $app "*") $layout -Recurse
Copy-Item (Join-Path $here "Images") (Join-Path $layout "Images") -Recurse

[xml] $manifest = Get-Content (Join-Path $here "Package.appxmanifest") -Raw
$identity = $manifest.Package.Identity
$version = $identity.Version
if ($identity.Name -like "*TODO*" -or $identity.Publisher -like "*TODO*") {
    # Package names can't contain "_", so the placeholders can't be packed as they are. Use a
    # stand-in identity for trying the package on this PC; Partner Center won't accept it.
    Write-Warning "Package.appxmanifest still has the TODO_FROM_PARTNER_CENTER placeholders. Packing with a local test identity: fine for trying it here, not for uploading."
    $identity.Name = "Overskin.LocalTest"
    $identity.Publisher = "CN=Overskin Local Test"
    $manifest.Package.Properties.PublisherDisplayName = "Overskin (local test)"
}
$publisher = $identity.Publisher
$manifest.Save((Join-Path $layout "AppxManifest.xml"))

# the images come in sizes (scale-200, targetsize-48, ...): resources.pri tells Windows which is which
$priConfig = Join-Path $work "priconfig.xml"
& $makepri createconfig /cf $priConfig /dq en-US /pv 10.0.0 /o | Out-Null
if ($LASTEXITCODE -ne 0) { throw "makepri createconfig failed" }
& $makepri new /pr $layout /cf $priConfig /mn (Join-Path $layout "AppxManifest.xml") /of (Join-Path $layout "resources.pri") /o | Out-Null
if ($LASTEXITCODE -ne 0) { throw "makepri failed" }

# ---- 3. pack -------------------------------------------------------------------------------------
$msix = Join-Path $dist "Overskin_${version}_x64.msix"
& $makeappx pack /d $layout /p $msix /o /h SHA256 | Out-Null
if ($LASTEXITCODE -ne 0) { throw "makeappx failed" }
Write-Host "Built $msix (unsigned: upload it to Partner Center, which signs it)"

# ---- 4. optional: a signed copy for installing on this PC --------------------------------------
if ($TestSign) {
    $signtool = Find-SdkTool "signtool.exe"
    $cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $publisher -and $_.NotAfter -gt (Get-Date) } | Select-Object -First 1
    if (-not $cert) {
        $cert = New-SelfSignedCertificate -Type Custom -Subject $publisher -KeyUsage DigitalSignature `
            -FriendlyName "Overskin package test signing" -CertStoreLocation "Cert:\CurrentUser\My" `
            -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}")
    }
    $cer = Join-Path $work "OverskinTest.cer"
    Export-Certificate -Cert $cert -FilePath $cer | Out-Null

    $test = Join-Path $dist "Overskin_${version}_x64.test.msix"
    Copy-Item $msix $test -Force
    & $signtool sign /fd SHA256 /sha1 $cert.Thumbprint /s My $test | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "signtool failed" }

    Write-Host ""
    Write-Host "Built $test, signed with a test certificate ($publisher)."
    Write-Host "To install it, trust the certificate once from an administrator PowerShell:"
    Write-Host "    Import-Certificate -FilePath `"$cer`" -CertStoreLocation Cert:\LocalMachine\TrustedPeople"
    Write-Host "then:"
    Write-Host "    Add-AppxPackage `"$test`""
}
