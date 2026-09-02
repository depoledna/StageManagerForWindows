# End-to-end Stage Manager behaviour test: builds app + StageProbe, restarts
# StageManager, spawns 6 coloured probe scenes (probe6 = 2 windows) and drives
# scene switches, checking that scenes are created, grouped by process, swapped
# on focus and torn down on exit. Each state is screenshot and the visible cards
# are additionally measured against the macOS geometry law (CARD_QUAD_SPEC.md).
# Report: tools\StageProbe\out\summary.json
param(
    [switch]$SkipBuild,
    [switch]$KeepProbes,
    [string]$Switches = 'probe1,probe4,probe6,probe2,probe5,probe3'
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent

# Read the target framework out of the csproj rather than hardcoding it: when the
# TFM last changed, a pinned path kept launching a weeks-old binary while the
# build quietly succeeded elsewhere, so every run measured stale code.
function Get-OutDir($csproj, $config = 'Debug') {
    $tfm = ([xml](Get-Content $csproj)).Project.PropertyGroup.TargetFramework | Where-Object { $_ } | Select-Object -First 1
    Join-Path (Split-Path $csproj -Parent) "bin\$config\$tfm"
}

$appProj = Join-Path $repo 'StageManager\StageManager.csproj'
$probeProj = Join-Path $repo 'tools\StageProbe\StageProbe.csproj'
$appDir = Get-OutDir $appProj
$appExe = Join-Path $appDir 'StageManager.exe'
$logFile = Join-Path $appDir 'stagemanager.log'
$probeExe = Join-Path (Get-OutDir $probeProj) 'StageProbe.exe'
$outDir = Join-Path $repo 'tools\StageProbe\out'

# Close BEFORE building: a running StageManager holds a lock on its own exe, so
# the copy step fails with MSB3026 and the build "succeeds" while leaving the old
# binary in place — the test would then silently measure the previous build.
# Graceful close: WM_CLOSE -> OnClosed -> SceneManager.Dispose restores every
# parked window. A hard kill would strand hidden windows off-screen.
$app = Get-Process StageManager -ErrorAction SilentlyContinue
if ($app) {
    $null = $app.CloseMainWindow()
    if (-not $app.WaitForExit(8000)) { $app | Stop-Process -Force }
    Start-Sleep -Milliseconds 500
}

if (-not $SkipBuild) {
    dotnet build $appProj | Select-Object -Last 3
    if ($LASTEXITCODE -ne 0) { throw 'StageManager build failed' }
    dotnet build $probeProj | Select-Object -Last 3
    if ($LASTEXITCODE -ne 0) { throw 'StageProbe build failed' }
}
# The exe must be newer than the sources it was built from, or the run is measuring
# a stale binary — the failure mode this whole path is guarding against.
$newestSource = Get-ChildItem (Join-Path $repo 'StageManager') -Recurse -Include *.cs, *.xaml |
    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ((Get-Item $appExe).LastWriteTime -lt $newestSource.LastWriteTime) {
    throw "stale binary: $appExe is older than $($newestSource.Name)"
}
if (Test-Path $logFile) { Remove-Item $logFile -Force }
Start-Process $appExe -WorkingDirectory $appDir
Start-Sleep -Seconds 5   # startup slide + initial window sweep

$probeArgs = @('run', '--log', $logFile, '--out', $outDir, '--switches', $Switches)
if ($KeepProbes) { $probeArgs += '--keep' }
$p = Start-Process $probeExe -ArgumentList $probeArgs -Wait -PassThru
Write-Host "report: $outDir\summary.json (exit $($p.ExitCode))"
exit $p.ExitCode
