<#
.SYNOPSIS
  Full VEDAR build: models → compile → tests → self-contained publish → Windows installer.
.EXAMPLE
  pwsh build/build.ps1
  pwsh build/build.ps1 -SkipTests
  powershell -ExecutionPolicy Bypass -File build\build.ps1
.NOTES
  Result: dist/VEDAR-Setup-v0.1.0-x64.exe
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [switch]$SkipTests,
    [switch]$SkipInstaller,
    [switch]$SkipModels
)
. (Join-Path $PSScriptRoot 'common.ps1')
$started = Get-Date
$dotnet = Get-DotNet
Write-Step "VEDAR $script:VedarVersion build ($(if (Test-OnWindows) { 'Windows' } else { 'cross-build from ' + [Environment]::OSVersion.Platform }))"
$sdk = (& $dotnet --version)
Write-Host "   .NET SDK $sdk" -ForegroundColor DarkGray
if (-not $sdk.StartsWith('10.')) { throw ".NET SDK 10 is required (found $sdk)." }

if (-not $SkipModels) { & (Join-Path $PSScriptRoot 'fetch-models.ps1') }

Write-Step 'Compiling'
Invoke-Native $dotnet @('build', (Join-Path $script:RepoRoot 'Vedar.sln'), '-c', $Configuration)

if (-not $SkipTests) {
    Write-Step 'Running automated tests'
    $frameworks = if (Test-OnWindows) { @('net10.0', 'net10.0-windows') } else { @('net10.0') }
    foreach ($f in $frameworks) {
        Invoke-Native $dotnet @('test', '--project', (Join-Path $script:RepoRoot 'tests/Vedar.Tests/Vedar.Tests.csproj'), '-c', $Configuration, '-f', $f, '--no-build')
    }
}

& (Join-Path $PSScriptRoot 'publish.ps1') -Configuration $Configuration
if (-not $SkipInstaller) { & (Join-Path $PSScriptRoot 'create-installer.ps1') }

Write-Step ("Done in {0:mm\:ss}" -f ((Get-Date) - $started))
