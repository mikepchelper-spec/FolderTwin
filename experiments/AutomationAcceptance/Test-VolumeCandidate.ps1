#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PackageZip,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{64}$')][string]$ZipSha256,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{64}$')][string]$ManifestSha256,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$SourceRevision,
    [switch]$VerifyOnly
)
$ErrorActionPreference = 'Stop'
if (!$VerifyOnly -and (!$IsWindows -or $env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted')) {
    throw 'The volume test requires a disposable GitHub-hosted Windows runner. Use -VerifyOnly for local package verification.'
}
$zip = (Resolve-Path -LiteralPath $PackageZip).Path
if ((Get-FileHash -LiteralPath $zip).Hash.ToLowerInvariant() -cne $ZipSha256) {
    throw 'The ZIP does not match the expected SHA-256.'
}
$stage = if ($VerifyOnly) { [IO.Directory]::CreateTempSubdirectory('FolderTwin-VolumeCandidate-').FullName }
         else { [IO.Path]::GetFullPath($env:RUNNER_TEMP) }
$package = Join-Path $stage 'FolderTwin-volume-package'
if (Test-Path -LiteralPath $package) { throw 'The candidate extraction directory must be new.' }
Expand-Archive -LiteralPath $zip -DestinationPath $package
$manifest = Join-Path $package 'SHA256SUMS.txt'
if ((Get-FileHash -LiteralPath $manifest).Hash.ToLowerInvariant() -cne $ManifestSha256) {
    throw 'The extracted manifest does not match the expected SHA-256.'
}
& (Join-Path $PSScriptRoot '../../scripts/Test-Portable.ps1') -PackageDirectory $package -SourceRevision $SourceRevision
Write-Output "Verified revision $SourceRevision. ZIP SHA-256 $ZipSha256. Manifest SHA-256 $ManifestSha256."

# Run the exact lab script shipped in the already-verified source archive.
$labSource = if ($VerifyOnly) { $stage } else { [IO.Directory]::CreateTempSubdirectory('FolderTwin-VolumeLabSource-').FullName }
$labRelativePath = 'experiments/CopyProbe/Run-VolumeLab.ps1'
& tar -xf (Join-Path $package 'FolderTwin-source.tar') -C $labSource $labRelativePath
if ($LASTEXITCODE -ne 0) { throw 'The lab script could not be extracted from the verified source archive.' }
$lab = Join-Path $labSource $labRelativePath
$tokens = $null
$errors = $null
[void][System.Management.Automation.Language.Parser]::ParseFile($lab, [ref]$tokens, [ref]$errors)
if ($errors.Count -ne 0) { throw 'The archived lab script is not valid PowerShell.' }
if ($VerifyOnly) {
    Write-Output "Verified extraction at $package and parsed its archived lab script. The volume experiment requires the hosted runner."
    return
}
& $lab
