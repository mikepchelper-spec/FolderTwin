#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Kopia,
    [Parameter(Mandatory)][string]$Rclone,
    [string]$Dotnet = (Get-Command dotnet).Source
)
$ErrorActionPreference = 'Stop'
$Kopia = (Resolve-Path -LiteralPath $Kopia).ProviderPath
$Rclone = (Resolve-Path -LiteralPath $Rclone).ProviderPath
if ((Get-FileHash -LiteralPath $Kopia).Hash -ne '96C0946314B50DCC7C5AE9D3EC7E3763E742FCEBF7D5CBEBDAAC3CEEE5FB3BD7' -or
    (Get-FileHash -LiteralPath $Rclone).Hash -ne 'ACD80683E23151E93BBE0D6FFA74D26E542EC510332F630BEECB9944F8D1A623') {
    throw 'The Kopia or rclone executable does not match the pinned acceptance version.'
}

$fixture = [IO.Directory]::CreateTempSubdirectory('FolderTwin-CloudS3-').FullName
$storage = Join-Path $fixture 's3'
[void][IO.Directory]::CreateDirectory((Join-Path $storage 'foldertwin'))
Write-Output "Synthetic S3 workspace: $fixture"
$project = Join-Path $PSScriptRoot '../CopyProbe/CopyProbe.csproj'
$artifacts = Join-Path $fixture 'build'
& $Dotnet build $project --artifacts-path $artifacts --nologo
if ($LASTEXITCODE -ne 0) { throw 'CopyProbe build failed.' }
$probe = Join-Path $artifacts 'bin/CopyProbe/debug/CopyProbe.dll'

$access = 'foldertwin-synthetic'
$secret = [Guid]::NewGuid().ToString('N')
$server = [Diagnostics.ProcessStartInfo]::new($Rclone)
$server.UseShellExecute = $false
$server.CreateNoWindow = $true
$server.RedirectStandardError = $true
foreach ($name in @($server.Environment.Keys)) {
    if ($name -like 'RCLONE_*') { [void]$server.Environment.Remove($name) }
}
# rclone parses the string-array environment flag as CSV. The pair is one field.
$server.Environment['RCLONE_AUTH_KEY'] = '"' + $access + ',' + $secret + '"'
foreach ($argument in 'serve', 's3', ":local:$storage", '--addr', '127.0.0.1:0', '--config', 'NUL', '--log-level', 'INFO') {
    $server.ArgumentList.Add($argument)
}

$process = [Diagnostics.Process]::Start($server)
$savedAccess = [Environment]::GetEnvironmentVariable('AWS_ACCESS_KEY_ID')
$savedSecret = [Environment]::GetEnvironmentVariable('AWS_SECRET_ACCESS_KEY')
$savedToken = [Environment]::GetEnvironmentVariable('AWS_SESSION_TOKEN')
try {
    $clock = [Diagnostics.Stopwatch]::StartNew()
    do {
        $line = $process.StandardError.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(5)).GetAwaiter().GetResult()
        if ($null -eq $line) { throw 'The synthetic S3 service exited during startup.' }
        if ($line -match 'http://127\.0\.0\.1:([1-9]\d*)') { $endpoint = "127.0.0.1:$($Matches[1])"; break }
    } while ($clock.Elapsed.TotalSeconds -lt 10)
    if (!$endpoint -or $process.HasExited) { throw 'The synthetic S3 service did not bind to loopback.' }
    $drain = $process.StandardError.ReadToEndAsync()
    [Environment]::SetEnvironmentVariable('AWS_ACCESS_KEY_ID', $access)
    [Environment]::SetEnvironmentVariable('AWS_SECRET_ACCESS_KEY', $secret)
    [Environment]::SetEnvironmentVariable('AWS_SESSION_TOKEN', $null)
    & $Dotnet $probe --cloud-point $Kopia --s3-lab $endpoint
    if ($LASTEXITCODE -ne 0) { throw 'Synthetic S3 acceptance failed.' }
} finally {
    [Environment]::SetEnvironmentVariable('AWS_ACCESS_KEY_ID', $savedAccess)
    [Environment]::SetEnvironmentVariable('AWS_SECRET_ACCESS_KEY', $savedSecret)
    [Environment]::SetEnvironmentVariable('AWS_SESSION_TOKEN', $savedToken)
    if (!$process.HasExited) { $process.Kill(); $process.WaitForExit() }
    $process.Dispose()
}
