<#
.SYNOPSIS
  Compiles the Windows installer dist/VEDAR-Setup-v0.1.0-x64.exe with Inno Setup.
.DESCRIPTION
  Looks for an installed Inno Setup 6 (ISCC.exe). If none is found it uses the pinned, checksum-verified
  Inno Setup binaries from NuGet (Tools.InnoSetup) and checks their Authenticode signature (Pyrsys B.V.,
  the Inno Setup publisher). On Linux/macOS ISCC.exe runs under Wine.
#>
[CmdletBinding()]
param(
    [string]$PublishDir = (Join-Path (Resolve-Path (Join-Path $PSScriptRoot '..')).Path 'artifacts/publish/win-x64'),
    [string]$OutputDir = (Join-Path (Resolve-Path (Join-Path $PSScriptRoot '..')).Path 'dist'),
    [switch]$NoDownload
)
. (Join-Path $PSScriptRoot 'common.ps1')
$deps = Get-Dependencies

function Find-Iscc {
    if ($env:INNO_SETUP_ISCC -and (Test-Path $env:INNO_SETUP_ISCC)) { return $env:INNO_SETUP_ISCC }
    if (Test-OnWindows) {
        foreach ($key in @('HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1',
                           'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1',
                           'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1')) {
            $loc = (Get-ItemProperty -Path $key -ErrorAction SilentlyContinue).InstallLocation
            if ($loc -and (Test-Path (Join-Path $loc 'ISCC.exe'))) { return (Join-Path $loc 'ISCC.exe') }
        }
        foreach ($p in @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe", "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe")) {
            if ($p -and (Test-Path $p)) { return $p }
        }
    }
    $local = Join-Path $script:RepoRoot 'build/.tools/innosetup/tools/ISCC.exe'
    if (Test-Path $local) { return $local }
    return $null
}

Write-Step 'Locating Inno Setup'
$iscc = Find-Iscc
if (-not $iscc) {
    if ($NoDownload) { throw 'Inno Setup 6 was not found. Install it from https://jrsoftware.org/isdl.php and run this script again.' }
    $toolsDir = Join-Path $script:RepoRoot 'build/.tools/innosetup'
    $nupkg = Join-Path $script:RepoRoot "build/.tools/tools.innosetup.$($deps.innoSetup.version).nupkg"
    Invoke-VerifiedDownload -Url $deps.innoSetup.nupkgUrl -Destination $nupkg -Sha256 $deps.innoSetup.sha256
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    if (Test-Path $toolsDir) { Remove-Item -Recurse -Force $toolsDir }
    [IO.Compression.ZipFile]::ExtractToDirectory($nupkg, $toolsDir)
    $iscc = Join-Path $toolsDir 'tools/ISCC.exe'
}
if (Test-OnWindows) {
    $sig = Get-AuthenticodeSignature $iscc
    if ($sig.Status -ne 'Valid') { throw "ISCC.exe signature is not valid: $($sig.Status)" }
    Write-Host "   $iscc (signed by $($sig.SignerCertificate.Subject))" -ForegroundColor DarkGray
} else {
    Write-Host "   $iscc (via Wine)" -ForegroundColor DarkGray
}

if (-not (Test-Path (Join-Path $PublishDir 'VEDAR.exe'))) { throw "Publish output not found in $PublishDir. Run build/publish.ps1 first." }
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

function ConvertTo-IsccPath([string]$Path) {
    if (Test-OnWindows) { return $Path }
    return (& winepath -w $Path 2>$null).Trim()
}

$script = Join-Path $script:RepoRoot 'installer/VEDAR.iss'
$isccArgs = @('/Q', "/DAppVersion=$script:VedarVersion", "/DSourceDir=$(ConvertTo-IsccPath $PublishDir)", "/DOutputDir=$(ConvertTo-IsccPath $OutputDir)", (ConvertTo-IsccPath $script))
Write-Step 'Compiling the installer'
if (Test-OnWindows) {
    Invoke-Native $iscc $isccArgs
} else {
    if (-not (Get-Command wine -ErrorAction SilentlyContinue)) { throw 'Wine is required to run Inno Setup on Linux/macOS (or build on Windows).' }
    $env:WINEDEBUG = '-all'
    if (-not $env:LANG -or $env:LANG -notmatch 'UTF-8') { $env:LANG = 'C.UTF-8' } # Wine maps Unix file names with the locale
    Invoke-Native 'wine' (@($iscc) + $isccArgs)
}

$setup = Join-Path $OutputDir "VEDAR-Setup-v$script:VedarVersion-x64.exe"
if (-not (Test-Path $setup)) { throw "The installer was not created: $setup" }
$hash = Get-Sha256 $setup
"$hash  $(Split-Path -Leaf $setup)" | Set-Content -Encoding ASCII "$setup.sha256"
Write-Host ("   Installer: {0} ({1:N0} MB)`n   SHA-256: {2}" -f $setup, ((Get-Item $setup).Length / 1MB), $hash) -ForegroundColor Green
