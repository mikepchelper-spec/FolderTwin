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

Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class IsolatedWindowProbe {
    public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    public static IntPtr FindVisibleWindowByTitle(int processId, string expectedTitle) {
        IntPtr found = IntPtr.Zero;
        EnumWindows((hWnd, _) => {
            if (!IsWindowVisible(hWnd)) return true;
            GetWindowThreadProcessId(hWnd, out uint pid);
            if (pid != (uint)processId) return true;
            var sb = new StringBuilder(256);
            GetWindowText(hWnd, sb, sb.Capacity);
            if (string.Equals(sb.ToString(), expectedTitle, StringComparison.Ordinal)) {
                found = hWnd;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }
}
"@

$registry = Join-Path $local 'FolderTwin/jobs'
if (Test-Path -LiteralPath $registry) { throw 'Expected a fresh FolderTwin profile.' }
$start = New-IsolatedStart (Join-Path $package 'FolderTwin.exe')
$process = [Diagnostics.Process]::Start($start)
try {
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $routeWindow = [IntPtr]::Zero
    while (!$process.HasExited -and $clock.Elapsed.TotalSeconds -lt 30) {
        $process.Refresh()
        $routeWindow = [IsolatedWindowProbe]::FindVisibleWindowByTitle($process.Id, 'FolderTwin · Trabajos')
        if ($routeWindow -ne [IntPtr]::Zero -and (Test-Path -LiteralPath $registry -PathType Container)) { break }
        Start-Sleep -Milliseconds 100
    }
    if ($process.HasExited -or $routeWindow -eq [IntPtr]::Zero -or !(Test-Path -LiteralPath $registry -PathType Container)) {
        throw 'No-argument startup did not open Ruta and create the synthetic registry.'
    }
    $clock.Restart()
    while (!$process.HasExited -and $clock.Elapsed.TotalSeconds -lt 15) {
        $targetWindow = [IsolatedWindowProbe]::FindVisibleWindowByTitle($process.Id, 'FolderTwin · Trabajos')
        if ($targetWindow -ne [IntPtr]::Zero) {
            [void][IsolatedWindowProbe]::PostMessage($targetWindow, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
        } else {
            [void]$process.CloseMainWindow()
        }
        Start-Sleep -Milliseconds 100
    }
    if (!$process.HasExited -or $process.ExitCode -ne 0 -or @(Get-ChildItem -LiteralPath $registry -Directory -Force).Count -ne 0) {
        throw 'Closing Ruta did not preserve an empty synthetic registry with a successful exit.'
    }
    'PASS public FolderTwin.exe opened Ruta without arguments and closed without creating jobs in the isolated profile.' |
        Tee-Object -FilePath (Join-Path $fixture 'result.txt')
} finally {
    if (!$process.HasExited) { $process.Kill(); $process.WaitForExit() }
    $process.Dispose()
}
