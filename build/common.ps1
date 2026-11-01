# Shared helpers for the VEDAR build scripts. Works with Windows PowerShell 5.1 and PowerShell 7 (Windows/Linux).
Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$script:RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$script:VedarVersion = '0.1.0'

function Test-OnWindows {
    if (Get-Variable -Name IsWindows -Scope Global -ErrorAction SilentlyContinue) { return [bool]$global:IsWindows }
    return $true # Windows PowerShell 5.1 only runs on Windows
}

function Write-Step([string]$Message) {
    Write-Host ''
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Get-Dependencies {
    Get-Content -Raw -Encoding UTF8 (Join-Path $PSScriptRoot 'dependencies.json') | ConvertFrom-Json
}

function Get-Sha256([string]$Path) {
    (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash.ToLowerInvariant()
}

function Get-DotNet {
    $candidates = @()
    if ($env:DOTNET_ROOT) { $candidates += (Join-Path $env:DOTNET_ROOT 'dotnet'); $candidates += (Join-Path $env:DOTNET_ROOT 'dotnet.exe') }
    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($cmd) { $candidates += $cmd.Source }
    $candidates += '/opt/dotnet/dotnet'
    foreach ($c in $candidates) { if ($c -and (Test-Path $c)) { return $c } }
    throw 'The .NET 10 SDK was not found. Install it from https://dot.net (only needed to build VEDAR, not to use it).'
}

function Invoke-Native {
    param([Parameter(Mandatory)][string]$FilePath, [string[]]$Arguments = @(), [string]$WorkingDirectory = $script:RepoRoot)
    Write-Host "   $FilePath $($Arguments -join ' ')" -ForegroundColor DarkGray
    Push-Location $WorkingDirectory
    try {
        & $FilePath @Arguments
        if ($LASTEXITCODE -ne 0) { throw "Command failed with exit code ${LASTEXITCODE}: $FilePath" }
    } finally { Pop-Location }
}

# Downloads a file to a temporary name, verifies SHA-256 and only then moves it into place.
function Invoke-VerifiedDownload {
    param([Parameter(Mandatory)][string]$Url, [Parameter(Mandatory)][string]$Destination, [Parameter(Mandatory)][string]$Sha256)
    if ((Test-Path -LiteralPath $Destination) -and ((Get-Sha256 $Destination) -eq $Sha256.ToLowerInvariant())) {
        Write-Host "   ok (cached) $Destination" -ForegroundColor DarkGray
        return
    }
    $dir = Split-Path -Parent $Destination
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    $temp = "$Destination.part"
    for ($attempt = 1; $attempt -le 4; $attempt++) {
        try {
            Write-Host "   downloading $Url" -ForegroundColor DarkGray
            Invoke-WebRequest -Uri $Url -OutFile $temp -UseBasicParsing
            break
        } catch {
            if ($attempt -eq 4) { throw }
            Start-Sleep -Seconds ([math]::Pow(2, $attempt))
        }
    }
    $actual = Get-Sha256 $temp
    if ($actual -ne $Sha256.ToLowerInvariant()) {
        Remove-Item -Force $temp
        throw "SHA-256 mismatch for ${Url}: expected $Sha256, got $actual"
    }
    Move-Item -Force $temp $Destination
    Write-Host "   verified $Destination" -ForegroundColor Green
}

# Carves cabinet files (MSCF) embedded in an executable such as the WiX Burn based vc_redist.x64.exe.
function Export-EmbeddedCabinets {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$OutputDirectory)
    New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
    $bytes = [System.IO.File]::ReadAllBytes($Path)
    $found = @()
    $i = [Array]::IndexOf($bytes, [byte]0x4D, 0)
    while ($i -ge 0 -and $i -lt $bytes.Length - 16) {
        $next = $i + 1
        if ($bytes[$i + 1] -eq 0x53 -and $bytes[$i + 2] -eq 0x43 -and $bytes[$i + 3] -eq 0x46 -and
            $bytes[$i + 4] -eq 0 -and $bytes[$i + 5] -eq 0 -and $bytes[$i + 6] -eq 0 -and $bytes[$i + 7] -eq 0) {
            $size = [BitConverter]::ToUInt32($bytes, $i + 8)
            if ($size -gt 64 -and ($i + $size) -le $bytes.Length) {
                $file = Join-Path $OutputDirectory ("cab{0}.cab" -f $found.Count)
                $segment = New-Object byte[] $size
                [Array]::Copy($bytes, $i, $segment, 0, $size)
                [System.IO.File]::WriteAllBytes($file, $segment)
                $found += $file
                $next = $i + [int]$size
            }
        }
        if ($next -ge $bytes.Length) { break }
        $i = [Array]::IndexOf($bytes, [byte]0x4D, $next)
    }
    return $found
}

function Test-IsCabinet([string]$Path) {
    $fs = [System.IO.File]::OpenRead($Path)
    try {
        $head = New-Object byte[] 4
        return ($fs.Read($head, 0, 4) -eq 4) -and ([System.Text.Encoding]::ASCII.GetString($head) -eq 'MSCF')
    } finally { $fs.Dispose() }
}

# Extracts all files (or the named files) from a cabinet with expand.exe (Windows) or cabextract (Linux/macOS).
function Expand-Cabinet([string]$Cabinet, [string]$Destination, [string]$FileName = '*') {
    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    if (Test-OnWindows) {
        & expand.exe $Cabinet "-F:$FileName" $Destination | Out-Null
    } elseif ($FileName -eq '*') {
        & cabextract -q -d $Destination $Cabinet 2>$null | Out-Null
    } else {
        & cabextract -q -F $FileName -d $Destination $Cabinet 2>$null | Out-Null
    }
}
