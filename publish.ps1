[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [string]$Runtime = "win-x64",

    [switch]$SelfContained,

    [switch]$Zip
)

$ErrorActionPreference = "Stop"

$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$Project = Join-Path $Root "src\Pixora\Pixora.csproj"
$Output = Join-Path $Root "publish\Pixora-$Runtime"
$ZipPath = "$Output.zip"

if (Test-Path $Output) {
    Remove-Item -LiteralPath $Output -Recurse -Force
}

if (Test-Path $ZipPath) {
    Remove-Item -LiteralPath $ZipPath -Force
}

$selfContainedValue = if ($SelfContained) { "true" } else { "false" }

dotnet publish $Project `
    --configuration $Configuration `
    --runtime $Runtime `
    --self-contained $selfContainedValue `
    --output $Output

# Windows PowerShell 5.1 does not apply $ErrorActionPreference to native command
# exit codes, so a failed publish would otherwise be packaged as if it succeeded.
# Keep this script ASCII-only: PS 5.1 reads BOM-less .ps1 files using the system
# code page, so non-ASCII characters here break parsing.
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

Write-Host "Published to: $Output"

if ($Zip) {
    Compress-Archive -Path (Join-Path $Output "*") -DestinationPath $ZipPath -CompressionLevel Optimal
    Write-Host "Packaged to: $ZipPath"
}
