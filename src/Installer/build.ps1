# Builds OverskinSetup.exe: the setup app with Overskin.exe appended to it.
#
#   ./src/Installer/build.ps1 -Payload dist/Overskin.exe -Out dist/OverskinSetup.exe   (./build.ps1 at the repo root runs this for you)
#
# Layout of the result: [setup exe][Overskin.exe][payload length, 8 bytes LE]["OVSKPAY1"]

param(
    [Parameter(Mandatory = $true)] [string] $Payload,
    [string] $Out = "OverskinSetup.exe",
    [string] $Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$project = Join-Path $root "OverskinSetup.csproj"

dotnet build $project -c $Configuration --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw "Installer build failed" }

$engine = Join-Path $root "bin/$Configuration/net472/OverskinSetup.exe"
$payloadPath = (Resolve-Path $Payload).Path
$outPath = if ([IO.Path]::IsPathRooted($Out)) { $Out } else { Join-Path (Get-Location) $Out }
$outPath = [IO.Path]::GetFullPath($outPath)

$output = [IO.File]::Create($outPath)
try {
    foreach ($file in @($engine, $payloadPath)) {
        $input = [IO.File]::OpenRead($file)
        try { $input.CopyTo($output) } finally { $input.Dispose() }
    }
    $length = (Get-Item $payloadPath).Length
    $trailer = [BitConverter]::GetBytes([long]$length) + [Text.Encoding]::ASCII.GetBytes("OVSKPAY1")
    $output.Write($trailer, 0, $trailer.Length)
}
finally {
    $output.Dispose()
}

Write-Host "Wrote $outPath ($([math]::Round((Get-Item $outPath).Length / 1MB, 1)) MB)"
