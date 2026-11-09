#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PackageDirectory,
    [Parameter(Mandatory)][string]$SourceRevision
)
$ErrorActionPreference = 'Stop'
$package = (Resolve-Path -LiteralPath $PackageDirectory).Path
& (Join-Path $PSScriptRoot '../../scripts/Test-Portable.ps1') -PackageDirectory $package -SourceRevision $SourceRevision

$fixture = [IO.Directory]::CreateTempSubdirectory('FolderTwin-IsolatedStartup-').FullName
$profile = Join-Path $fixture 'profile'
$local = Join-Path $profile 'AppData/Local'
$roaming = Join-Path $profile 'AppData/Roaming'
$temp = Join-Path $profile 'Temp'
foreach ($path in $local, $roaming, $temp) { [IO.Directory]::CreateDirectory($path) | Out-Null }
Write-Output "Synthetic startup evidence: $fixture"

function New-IsolatedStart([string]$program) {
    $start = [Diagnostics.ProcessStartInfo]::new($program)
    $start.WorkingDirectory = $package
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.Environment['USERPROFILE'] = $profile
    $start.Environment['LOCALAPPDATA'] = $local
    $start.Environment['APPDATA'] = $roaming
    $start.Environment['TEMP'] = $temp
    $start.Environment['TMP'] = $temp
    return $start
}

$preflight = New-IsolatedStart (Get-Command pwsh).Source
$preflight.RedirectStandardOutput = $true
foreach ($argument in @('-NoProfile', '-NonInteractive', '-Command', '[Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)')) {
    $preflight.ArgumentList.Add($argument)
}
$probe = [Diagnostics.Process]::Start($preflight)
try {
    if (!$probe.WaitForExit(10000)) { $probe.Kill(); throw 'The profile preflight timed out.' }
    if ($probe.ExitCode -ne 0 -or $probe.StandardOutput.ReadToEnd().Trim() -cne $local) {
        throw 'The child did not resolve the isolated LocalAppData. FolderTwin was not launched.'
    }
} finally { $probe.Dispose() }

$registry = Join-Path $local 'FolderTwin/jobs'
if (Test-Path -LiteralPath $registry) { throw 'Expected a fresh FolderTwin profile.' }
$start = New-IsolatedStart (Join-Path $package 'FolderTwin.exe')
$process = [Diagnostics.Process]::Start($start)
try {
    $clock = [Diagnostics.Stopwatch]::StartNew()
    while (!$process.HasExited -and $clock.Elapsed.TotalSeconds -lt 30) {
        $process.Refresh()
        if ($process.MainWindowTitle -ceq 'FolderTwin · Trabajos' -and (Test-Path -LiteralPath $registry -PathType Container)) { break }
        Start-Sleep -Milliseconds 100
    }
    if ($process.HasExited -or $process.MainWindowTitle -cne 'FolderTwin · Trabajos' -or !(Test-Path -LiteralPath $registry -PathType Container)) {
        throw 'No-argument startup did not open Ruta and create the synthetic registry.'
    }
    $clock.Restart()
    while (!$process.HasExited -and $clock.Elapsed.TotalSeconds -lt 15) { [void]$process.CloseMainWindow(); Start-Sleep -Milliseconds 100 }
    if (!$process.HasExited -or $process.ExitCode -ne 0 -or @(Get-ChildItem -LiteralPath $registry -Directory -Force).Count -ne 0) {
        throw 'Closing Ruta did not preserve an empty synthetic registry with a successful exit.'
    }
    'PASS public FolderTwin.exe opened Ruta without arguments and closed without creating jobs in the isolated profile.' |
        Tee-Object -FilePath (Join-Path $fixture 'result.txt')
} finally {
    if (!$process.HasExited) { $process.Kill(); $process.WaitForExit() }
    $process.Dispose()
}
