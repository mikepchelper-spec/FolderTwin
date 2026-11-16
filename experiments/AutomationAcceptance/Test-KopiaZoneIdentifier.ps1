#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Kopia
)

$ErrorActionPreference = 'Stop'
if (!$IsWindows) { throw 'This experiment requires Windows with a local NTFS temporary directory.' }
$Kopia = (Resolve-Path -LiteralPath $Kopia).ProviderPath
$toolHash = (Get-FileHash -LiteralPath $Kopia -Algorithm SHA256).Hash
if ($toolHash -ne '96C0946314B50DCC7C5AE9D3EC7E3763E742FCEBF7D5CBEBDAAC3CEEE5FB3BD7') {
    throw 'The Kopia executable differs from the pinned 0.23.1 Windows x64 tool.'
}
$fixture = [IO.Directory]::CreateTempSubdirectory('FolderTwin-KopiaZone-').FullName
$source = Join-Path $fixture 'source'
$repository = Join-Path $fixture 'repository'
$restored = Join-Path $fixture 'restored'
$writer = Join-Path $fixture 'writer.config'
$reader = Join-Path $fixture 'reader.config'
$readerCache = Join-Path $fixture 'reader-cache'
$password = [Guid]::NewGuid().ToString('N')
$completed = $false

function Invoke-Kopia([string]$Config, [string[]]$Arguments) {
    $start = [Diagnostics.ProcessStartInfo]::new($Kopia)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.RedirectStandardInput = $true
    foreach ($name in @($start.Environment.Keys)) {
        if ($name -like 'KOPIA_*' -or $name -like 'AWS_*') { [void]$start.Environment.Remove($name) }
    }
    $start.Environment['KOPIA_PASSWORD'] = $password
    $start.Environment['KOPIA_CHECK_FOR_UPDATES'] = 'false'
    foreach ($argument in @('--config-file', $Config, '--no-persist-credentials', '--no-use-credential-manager',
        '--no-auto-maintenance', '--disable-file-logging', '--disable-content-log', '--no-progress') + $Arguments) {
        $start.ArgumentList.Add($argument)
    }
    $process = [Diagnostics.Process]::Start($start)
    try {
        $process.StandardInput.Close()
        $output = $process.StandardOutput.ReadToEndAsync()
        $errors = $process.StandardError.ReadToEndAsync()
        if (!$process.WaitForExit(60000)) { throw 'The synthetic Kopia command exceeded one minute.' }
        $stdout = $output.GetAwaiter().GetResult()
        $stderr = $errors.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) { throw "Synthetic Kopia command failed with $($process.ExitCode). $stderr" }
        return $stdout
    } finally {
        if (!$process.HasExited) { $process.Kill($true); $process.WaitForExit() }
        $process.Dispose()
    }
}

function Read-Fingerprint([string]$Path) {
    # These reads are independent of Kopia and FolderTwin's metadata readers.
    $streams = @(Get-Item -LiteralPath $Path -Stream '*' | Where-Object Stream -eq 'Zone.Identifier')
    $zone = if ($streams.Count -eq 0) { $null } else {
        [Convert]::ToBase64String([IO.File]::ReadAllBytes($Path + ':Zone.Identifier'))
    }
    return [pscustomobject]@{
        MainHash = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
        ZonePresent = $streams.Count -eq 1
        ZoneBytes = if ($streams.Count -eq 0) { $null } else { $streams[0].Length }
        ZoneBase64 = $zone
    }
}

try {
    foreach ($folder in $source, $repository, $restored) { [void][IO.Directory]::CreateDirectory($folder) }
    $mainBytes = [Text.Encoding]::UTF8.GetBytes('Same synthetic main content for all four files.')
    $marks = [ordered]@{
        'internet.txt' = [Text.Encoding]::UTF8.GetBytes("[ZoneTransfer]`r`nZoneId=3`r`nHostUrl=https://synthetic.invalid/first`r`n")
        'restricted.txt' = [Text.Encoding]::UTF8.GetBytes("[ZoneTransfer]`nZoneId=4`nReferrerUrl=https://synthetic.invalid/second`n")
        'empty-zone.txt' = [byte[]]::new(0)
        'unmarked.txt' = $null
    }
    $expected = @{}
    foreach ($name in $marks.Keys) {
        $path = Join-Path $source $name
        [IO.File]::WriteAllBytes($path, $mainBytes)
        if ($null -ne $marks[$name]) { [IO.File]::WriteAllBytes($path + ':Zone.Identifier', $marks[$name]) }
        $expected[$name] = Read-Fingerprint $path
        if ($expected[$name].ZonePresent -ne ($null -ne $marks[$name])) { throw 'The synthetic ADS fixture was not created.' }
    }
    [void](Invoke-Kopia $writer @('repository', 'create', 'filesystem', '--path', $repository,
        '--cache-directory', (Join-Path $fixture 'writer-cache'), '--no-enable-actions'))
    [void](Invoke-Kopia $writer @('snapshot', 'create', '--force-hash=100', '--fail-fast', '--force-disable-actions', $source))
    $points = @(Invoke-Kopia $writer @('snapshot', 'list', '--all', '--json') | ConvertFrom-Json)
    if ($points.Count -ne 1 -or $points[0].rootEntry.summ.files -ne 4 -or $points[0].rootEntry.summ.numFailed -ne 0) {
        throw 'The snapshot did not contain all four synthetic files.'
    }
    $rootId = $points[0].rootEntry.obj
    if ($rootId -cnotmatch '^k[0-9a-f]{32}$') { throw 'Unexpected synthetic snapshot root.' }
    foreach ($name in $marks.Keys) {
        $observed = Read-Fingerprint (Join-Path $source $name)
        if ($observed.MainHash -ne $expected[$name].MainHash -or
            $observed.ZonePresent -ne $expected[$name].ZonePresent -or $observed.ZoneBase64 -cne $expected[$name].ZoneBase64) {
            throw 'Snapshot creation modified the synthetic source.'
        }
    }
    $resolvedSource = (Resolve-Path -LiteralPath $source).ProviderPath
    $aside = Join-Path $fixture 'source-aside'
    if ([IO.Path]::GetDirectoryName($resolvedSource) -ne $fixture -or
        [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($aside)) -ne $fixture) { throw 'Synthetic move left its fixture.' }
    Move-Item -LiteralPath $resolvedSource -Destination $aside
    if (Test-Path -LiteralPath $readerCache) { throw 'The recovery cache must start absent.' }
    [void](Invoke-Kopia $reader @('repository', 'connect', 'filesystem', '--path', $repository,
        '--cache-directory', $readerCache, '--readonly'))
    [void](Invoke-Kopia $reader @('snapshot', 'restore', $rootId, $restored, '--no-overwrite-files',
        '--no-overwrite-directories', '--no-overwrite-symlinks', '--no-ignore-permission-errors', '--write-files-atomically', '--flush-files'))
    $results = foreach ($name in $marks.Keys) {
        $observed = Read-Fingerprint (Join-Path $restored $name)
        $prior = $expected[$name]
        if ($observed.MainHash -ne $prior.MainHash) { throw 'Restored main bytes differ.' }
        [pscustomobject]@{
            Name = $name
            MainSha256 = $observed.MainHash
            MainMatches = $true
            ExpectedZonePresent = $prior.ZonePresent
            ActualZonePresent = $observed.ZonePresent
            ExpectedZoneBytes = $prior.ZoneBytes
            ActualZoneBytes = $observed.ZoneBytes
            ZoneMatches = $prior.ZonePresent -eq $observed.ZonePresent -and $prior.ZoneBase64 -ceq $observed.ZoneBase64
        }
    }
    [pscustomobject]@{
        SchemaVersion = 1
        KopiaVersion = '0.23.1'
        KopiaSha256 = $toolHash
        Backend = 'Disposable local filesystem repository'
        ReadOnlyRecovery = $true
        EmptyRecoveryCache = $true
        SourceApart = $true
        Results = @($results)
        AllZoneIdentifiersPreserved = @($results | Where-Object { !$_.ZoneMatches }).Count -eq 0
    } | ConvertTo-Json -Depth 5
    $completed = $true
} finally {
    if ($completed) {
        $resolved = (Resolve-Path -LiteralPath $fixture).ProviderPath
        if ([IO.Path]::GetDirectoryName($resolved) -ne [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetTempPath()) -or
            ![IO.Path]::GetFileName($resolved).StartsWith('FolderTwin-KopiaZone-', [StringComparison]::Ordinal)) {
            throw 'Synthetic cleanup target left the owned temporary scope.'
        }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    } else { Write-Warning "Synthetic fixture preserved at $fixture" }
}
