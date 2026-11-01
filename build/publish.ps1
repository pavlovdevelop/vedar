<#
.SYNOPSIS
  Creates the self-contained Windows x64 build of VEDAR (no .NET installation needed on the target PC).
.DESCRIPTION
  Output: artifacts/publish/win-x64 with VEDAR.exe, the .NET runtime, native speech libraries, the
  Microsoft Visual C++ runtime DLLs (app-local), the bundled speech models and the documentation.
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Output = (Join-Path (Resolve-Path (Join-Path $PSScriptRoot '..')).Path 'artifacts/publish/win-x64'),
    [string]$ModelsDir = (Join-Path (Resolve-Path (Join-Path $PSScriptRoot '..')).Path 'models'),
    [switch]$SkipModels
)
. (Join-Path $PSScriptRoot 'common.ps1')
$dotnet = Get-DotNet
$deps = Get-Dependencies

Write-Step "Publishing VEDAR $script:VedarVersion (self-contained, win-x64)"
if (Test-Path $Output) { Remove-Item -Recurse -Force $Output }
Invoke-Native $dotnet @('publish', (Join-Path $script:RepoRoot 'src/Vedar.Desktop/Vedar.Desktop.csproj'),
    '-c', $Configuration, '-r', 'win-x64', '--self-contained', 'true', '-o', $Output,
    '-p:DebugType=None', '-p:DebugSymbols=false', '-p:PublishReadyToRun=true')
$exe = Join-Path $Output 'VEDAR.exe'
if (-not (Test-Path $exe)) { throw "VEDAR.exe was not produced in $Output" }
# Whisper.net ships native libraries for every architecture; VEDAR targets x64 only.
Get-ChildItem (Join-Path $Output 'runtimes') -Directory -ErrorAction SilentlyContinue | Where-Object Name -ne 'win-x64' | Remove-Item -Recurse -Force

Write-Step 'Adding the Microsoft Visual C++ runtime (required by whisper.cpp), app-local'
$vcDir = Join-Path $script:RepoRoot 'build/.tools/vcredist'
$haveAll = $true
foreach ($dll in $deps.vcRedist.dlls) { if (-not (Test-Path (Join-Path $vcDir $dll))) { $haveAll = $false } }
if (-not $haveAll) {
    New-Item -ItemType Directory -Force -Path $vcDir | Out-Null
    $system32 = if (Test-OnWindows) { Join-Path $env:SystemRoot 'System32' } else { $null }
    $fromSystem = $system32 -and (@($deps.vcRedist.dlls | Where-Object { -not (Test-Path (Join-Path $system32 $_)) }).Count -eq 0)
    if ($fromSystem) {
        foreach ($dll in $deps.vcRedist.dlls) { Copy-Item (Join-Path $system32 $dll) $vcDir -Force }
    } else {
        $redist = Join-Path $vcDir 'VC_redist.x64.exe'
        Invoke-VerifiedDownload -Url $deps.vcRedist.url -Destination $redist -Sha256 $deps.vcRedist.sha256
        # The WiX bundle contains an attached cabinet, which in turn contains the MSI cabinets with the DLLs.
        $work = Join-Path $vcDir 'work'
        $outer = Export-EmbeddedCabinets -Path $redist -OutputDirectory (Join-Path $work 'outer')
        $n = 0
        foreach ($cab in $outer) { Expand-Cabinet $cab (Join-Path $work ("payload{0}" -f $n++)) }
        $inner = @(Get-ChildItem -Recurse -File $work | Where-Object { Test-IsCabinet $_.FullName } | ForEach-Object FullName)
        foreach ($cab in $inner) {
            foreach ($dll in $deps.vcRedist.dlls) {
                if (Test-Path (Join-Path $vcDir $dll)) { continue }
                Expand-Cabinet $cab $vcDir "${dll}_amd64"
                $extracted = Join-Path $vcDir "${dll}_amd64"
                if (Test-Path $extracted) { Move-Item -Force $extracted (Join-Path $vcDir $dll) }
            }
        }
        Remove-Item -Recurse -Force $work
    }
}
foreach ($dll in $deps.vcRedist.dlls) {
    $src = Join-Path $vcDir $dll
    if (-not (Test-Path $src)) { throw "Could not obtain $dll from the Visual C++ redistributable." }
    if (Test-OnWindows) {
        $sig = Get-AuthenticodeSignature $src
        if ($sig.Status -ne 'Valid' -or $sig.SignerCertificate.Subject -notmatch 'Microsoft Corporation') { throw "$dll is not validly signed by Microsoft." }
    }
    Copy-Item $src $Output -Force
}

if (-not $SkipModels) {
    Write-Step 'Bundling speech models (offline speech recognition and Bulgarian voice)'
    $modelsOut = Join-Path $Output 'models'
    foreach ($m in $deps.models) {
        $rel = $m.path -replace '/', [IO.Path]::DirectorySeparatorChar
        # The installer ships the prepared voice (.sherpa.onnx) instead of a second copy of the original weights.
        if ($m.path -like 'piper/*/*.onnx') { continue }
        $src = Join-Path $ModelsDir $rel
        if (-not (Test-Path $src)) { throw "Missing model $rel. Run build/fetch-models.ps1 first." }
        if ((Get-Sha256 $src) -ne $m.sha256) { throw "Checksum mismatch for $rel." }
        $dst = Join-Path $modelsOut $rel
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $dst) | Out-Null
        Copy-Item $src $dst -Force
    }
    $voiceDir = Join-Path $ModelsDir (Join-Path 'piper' 'bg_BG-dimitar-medium')
    foreach ($f in @('bg_BG-dimitar-medium.sherpa.onnx', 'tokens.txt')) {
        $src = Join-Path $voiceDir $f
        if (-not (Test-Path $src)) { throw "Missing prepared voice file $f. Run build/fetch-models.ps1 first." }
        Copy-Item $src (Join-Path $modelsOut (Join-Path 'piper' (Join-Path 'bg_BG-dimitar-medium' $f))) -Force
    }

    # Checksums of every bundled model file; the installer verifies them after installation.
    $sums = Get-ChildItem -Recurse -File $modelsOut | Sort-Object FullName | ForEach-Object {
        $rel = $_.FullName.Substring($modelsOut.Length + 1) -replace '\\', '/'
        "$(Get-Sha256 $_.FullName)  $rel"
    }
    [IO.File]::WriteAllLines((Join-Path $modelsOut 'SHA256SUMS.txt'), [string[]]$sums)
}

Write-Step 'Adding documentation and licenses'
$docsOut = Join-Path $Output 'docs'
New-Item -ItemType Directory -Force -Path $docsOut | Out-Null
foreach ($doc in @('README.md', 'USER_GUIDE_BG.md', 'INSTALLATION.md', 'ACCESSIBILITY_TESTS.md', 'THIRD_PARTY_LICENSES.md', 'CHANGELOG.md', 'LICENSE')) {
    $src = Join-Path $script:RepoRoot $doc
    if (Test-Path $src) { Copy-Item $src $docsOut -Force }
}
# Plain-text user guide: opens in Notepad, the most screen-reader-friendly format.
$guide = Join-Path $script:RepoRoot 'USER_GUIDE_BG.md'
if (Test-Path $guide) {
    $text = (Get-Content -Raw -Encoding UTF8 $guide) -replace '(?m)^#{1,6}\s*', '' -replace '\*\*', '' -replace '(?m)^\s*[-*]\s+', '- ' -replace '`', ''
    [IO.File]::WriteAllText((Join-Path $docsOut 'Ръководство на потребителя.txt'), $text, (New-Object System.Text.UTF8Encoding($true)))
}

$commit = try { (& git -C $script:RepoRoot rev-parse --short HEAD 2>$null) } catch { 'unknown' }
@"
VEDAR $script:VedarVersion
Build: $(Get-Date -Format 'yyyy-MM-dd HH:mm') UTC$(Get-Date -Format 'zzz'), commit $commit
"@ | Set-Content -Encoding UTF8 (Join-Path $Output 'build-info.txt')

$size = (Get-ChildItem -Recurse -File $Output | Measure-Object -Property Length -Sum).Sum
Write-Host ("   Published to {0} ({1:N0} MB)" -f $Output, ($size / 1MB)) -ForegroundColor Green
