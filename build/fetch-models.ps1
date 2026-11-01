<#
.SYNOPSIS
  Downloads the speech models VEDAR ships with and verifies their SHA-256 checksums.
.DESCRIPTION
  Whisper small-q5_1 and base-q5_1 (multilingual, whisper.cpp), the official Piper voice bg_BG-dimitar-medium
  and the Bulgarian espeak-ng data. Then prepares the Piper voice for the sherpa-onnx runtime (adds metadata to
  a copy of the model; the network weights are unchanged).
#>
[CmdletBinding()]
param(
    [string]$ModelsDir = (Join-Path (Resolve-Path (Join-Path $PSScriptRoot '..')).Path 'models')
)
. (Join-Path $PSScriptRoot 'common.ps1')

Write-Step "Fetching speech models into $ModelsDir"
$deps = Get-Dependencies
foreach ($m in $deps.models) {
    $target = Join-Path $ModelsDir ($m.path -replace '/', [IO.Path]::DirectorySeparatorChar)
    Invoke-VerifiedDownload -Url $m.url -Destination $target -Sha256 $m.sha256
}

Write-Step 'Preparing the Piper voice for sherpa-onnx'
$voiceDir = Join-Path $ModelsDir (Join-Path 'piper' 'bg_BG-dimitar-medium')
$prepared = Join-Path $voiceDir 'bg_BG-dimitar-medium.sherpa.onnx'
$tokens = Join-Path $voiceDir 'tokens.txt'
if ((Test-Path $prepared) -and (Test-Path $tokens) -and ((Get-Item $prepared).LastWriteTimeUtc -ge (Get-Item (Join-Path $voiceDir 'bg_BG-dimitar-medium.onnx')).LastWriteTimeUtc)) {
    Write-Host '   ok (already prepared)' -ForegroundColor DarkGray
} else {
    $dotnet = Get-DotNet
    Invoke-Native $dotnet @('run', '--project', (Join-Path $script:RepoRoot 'tools/Vedar.Tools/Vedar.Tools.csproj'), '-c', 'Release', '--',
        'prepare-piper', (Join-Path $voiceDir 'bg_BG-dimitar-medium.onnx'), $voiceDir)
}

$total = ($deps.models | Measure-Object -Property size -Sum).Sum
Write-Host ("   {0} files, {1:N0} MB, all checksums verified." -f $deps.models.Count, ($total / 1MB)) -ForegroundColor Green
