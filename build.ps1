# Builds the Overskin installer into dist\
#
#   ./build.ps1                 (or ./build.ps1 -Out somewhere\else)
#
# dist\OverskinSetup.exe   the one file to download: the installer, with Overskin inside it
#
# The app itself is built into build\app first; it's what the installer carries.

param([string] $Out = "")

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$dist = if ($Out) { [IO.Path]::GetFullPath($Out) } else { Join-Path $root "dist" }
$app = Join-Path $root "build\app"
New-Item -ItemType Directory -Force $dist, $app | Out-Null

dotnet publish (Join-Path $root "src/Overskin/Overskin.csproj") -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishReadyToRun=true -o $app --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw "Overskin build failed" }
Remove-Item (Join-Path $app "*.pdb") -ErrorAction SilentlyContinue

& (Join-Path $root "src/Installer/build.ps1") -Payload (Join-Path $app "Overskin.exe") -Out (Join-Path $dist "OverskinSetup.exe")

# older builds also left the bare app here; the installer is the only download now
Remove-Item (Join-Path $dist "Overskin.exe") -ErrorAction SilentlyContinue
