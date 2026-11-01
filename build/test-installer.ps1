<#
.SYNOPSIS
  End-to-end installer test on Windows: silent install → verify → start VEDAR → exit → uninstall → verify.
.DESCRIPTION
  Used by CI on a real Windows machine. Installs for the current user only, in a separate data folder.
#>
[CmdletBinding()]
param([string]$Installer = (Join-Path (Resolve-Path (Join-Path $PSScriptRoot '..')).Path 'dist/VEDAR-Setup-v0.1.0-x64.exe'))
. (Join-Path $PSScriptRoot 'common.ps1')
if (-not (Test-OnWindows)) { throw 'This test must run on Windows.' }
if (-not (Test-Path $Installer)) { throw "Installer not found: $Installer" }

$appDir = Join-Path $env:LOCALAPPDATA 'Programs\VEDAR'
$tempDir = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { $env:TEMP }
$log = Join-Path $tempDir 'vedar-setup.log'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{8F0E9A2C-5B1D-4E7A-9C3F-2D6B7A1E4F90}_is1'

Write-Step 'Silent installation (current user, with autostart)'
$p = Start-Process -FilePath $Installer -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/CURRENTUSER', '/TASKS=autostart', "/LOG=$log" -Wait -PassThru
if ($p.ExitCode -ne 0) { Get-Content $log -Tail 40; throw "Installer exit code $($p.ExitCode)" }
if (-not (Test-Path (Join-Path $appDir 'VEDAR.exe'))) { throw 'VEDAR.exe was not installed.' }
if (-not (Select-String -Path $log -Pattern 'All bundled speech models verified' -Quiet)) { throw 'Model verification did not run or failed.' }
$run = (Get-ItemProperty $runKey -ErrorAction SilentlyContinue).VEDAR
if ($run -notmatch '--background') { throw "Autostart entry missing or wrong: $run" }
$uninstall = Get-ItemProperty $uninstallKey -ErrorAction SilentlyContinue
if (-not $uninstall -or $uninstall.DisplayVersion -ne $script:VedarVersion) { throw 'Uninstall entry missing.' }
Write-Host "   installed: $appDir; autostart: $run" -ForegroundColor Green

Write-Step 'Starting the installed VEDAR in background mode (as after Windows sign-in)'
$data = Join-Path $env:TEMP ('vedar-ci-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $data | Out-Null
'{ "General": { "FirstRunCompleted": true, "GreetOnStartup": true }, "Activation": { "HandsFreeEnabled": true } }' | Set-Content -Encoding UTF8 (Join-Path $data 'settings.json')
$env:VEDAR_DATA_DIR = $data
$app = Start-Process -FilePath (Join-Path $appDir 'VEDAR.exe') -ArgumentList '--background' -PassThru
$deadline = (Get-Date).AddSeconds(120)
$logFile = $null
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 3
    $logFile = Get-ChildItem (Join-Path $data 'logs') -Filter 'vedar-*.log' -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($logFile -and (Select-String -Path $logFile.FullName -Pattern 'VEDAR started' -Quiet)) { break }
    if ($app.HasExited) { break }
}
if ($logFile) { Get-Content $logFile.FullName | Select-Object -First 40 | ForEach-Object { Write-Host "   | $_" } }
if (-not $logFile -or -not (Select-String -Path $logFile.FullName -Pattern 'VEDAR started' -Quiet)) { throw 'VEDAR did not report a successful start.' }
foreach ($pattern in @('Whisper loaded', 'Piper voice bg_BG-dimitar-medium loaded')) {
    if (-not (Select-String -Path $logFile.FullName -Pattern $pattern -Quiet)) { throw "Log does not contain '$pattern'." }
}
if ($app.HasExited) { throw "VEDAR exited unexpectedly (code $($app.ExitCode))." }

Write-Step 'Asking VEDAR to exit (VEDAR.exe --exit)'
Start-Process -FilePath (Join-Path $appDir 'VEDAR.exe') -ArgumentList '--exit' -Wait
if (-not $app.WaitForExit(20000)) { $app.Kill(); throw 'VEDAR did not exit on request.' }
Remove-Item Env:\VEDAR_DATA_DIR

Write-Step 'Silent uninstall'
$u = Start-Process -FilePath (Join-Path $appDir 'unins000.exe') -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -Wait -PassThru
Start-Sleep -Seconds 3
if (Test-Path (Join-Path $appDir 'VEDAR.exe')) { throw 'Files remain after uninstall.' }
if ((Get-ItemProperty $runKey -ErrorAction SilentlyContinue).VEDAR) { throw 'Autostart entry remains after uninstall.' }
if (Test-Path $uninstallKey) { throw 'Uninstall entry remains.' }
Write-Host '   Install, start, exit and uninstall verified on Windows.' -ForegroundColor Green
