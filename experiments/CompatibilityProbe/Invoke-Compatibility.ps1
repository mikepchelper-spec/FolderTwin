param(
    [Parameter(Mandatory)][string]$Dotnet,
    [Parameter(Mandatory)][string]$PreviousCore,
    [Parameter(Mandatory)][string]$CurrentCore,
    [Parameter(Mandatory)][string]$PreviousArchive,
    [string]$ExpectedArchiveSha256 = '2f795b694fa700c9993cb8cf8817017b39bed83d6212cc199eec71071bea7760'
)
$ErrorActionPreference = 'Stop'
$campaignClock = [Diagnostics.Stopwatch]::StartNew()
$previousCorePath = (Resolve-Path -LiteralPath $PreviousCore).Path
$currentCorePath = (Resolve-Path -LiteralPath $CurrentCore).Path
if ((Get-FileHash -LiteralPath $PreviousArchive -Algorithm SHA256).Hash -ine $ExpectedArchiveSha256) {
    throw 'Previous release ZIP does not match its accepted SHA-256.'
}
$previousHash = (Get-FileHash -LiteralPath $previousCorePath -Algorithm SHA256).Hash.ToLowerInvariant()
$currentHash = (Get-FileHash -LiteralPath $currentCorePath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($previousHash -eq $currentHash) { throw 'Compatibility requires different previous and current binaries.' }
$archive = [IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $PreviousArchive).Path)
try {
    $coreEntries = @($archive.Entries | Where-Object { $_.FullName -match '(^|/)CopyProbe\.dll$' })
    if ($coreEntries.Count -ne 1) { throw 'Expected exactly one CopyProbe.dll in the previous accepted ZIP.' }
    $stream = $coreEntries[0].Open()
    try { $archiveCoreHash = [Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData($stream)) }
    finally { $stream.Dispose() }
    if ($archiveCoreHash -ne $previousHash) { throw 'PreviousCore is not the core contained in the accepted ZIP.' }
}
finally { $archive.Dispose() }
$buildRoot = [IO.Directory]::CreateTempSubdirectory('FolderTwin-CompatibilityBuild-').FullName
$project = Join-Path $PSScriptRoot 'CompatibilityProbe.csproj'
foreach ($revision in @('old', 'new')) {
    if ($campaignClock.Elapsed.TotalSeconds -gt 60) { throw 'Compatibility campaign exceeded its 60-second cooperative budget.' }
    $core = if ($revision -eq 'old') { $previousCorePath } else { $currentCorePath }
    $artifacts = Join-Path $buildRoot $revision
    & $Dotnet build $project --artifacts-path $artifacts "-p:ReferenceCoreAssembly=$core" --disable-build-servers -v minimal
    if ($LASTEXITCODE -ne 0) { throw "Compatibility harness failed to build against $revision core." }
}
$oldHarness = Join-Path $buildRoot 'old/bin/CompatibilityProbe/debug/CopyLab.dll'
$newHarness = Join-Path $buildRoot 'new/bin/CompatibilityProbe/debug/CopyLab.dll'
$created = @(& $Dotnet $oldHarness --create)
$createExitCode = $LASTEXITCODE
$created | Write-Output
if ($createExitCode -ne 0) { throw 'Old core failed to create the compatibility fixture; preserve its output.' }
$fixtureLines = @($created | Where-Object { $_.StartsWith('FIXTURE=') })
if ($fixtureLines.Count -ne 1 -or $created -notcontains "CREATOR_CORE_SHA256=$previousHash") {
    throw 'Fixture provenance does not match the accepted old core.'
}
$fixture = $fixtureLines[0].Substring('FIXTURE='.Length)
$checked = @(& $Dotnet $newHarness --check $fixture)
$checkExitCode = $LASTEXITCODE
$checked | Write-Output
if ($checkExitCode -ne 0) { throw 'New core failed to open the old fixture; preserved for inspection.' }
if ($campaignClock.Elapsed.TotalSeconds -gt 60) { throw 'Compatibility campaign exceeded its 60-second cooperative budget.' }
$evidence = [ordered]@{
    PreviousArchiveSha256 = $ExpectedArchiveSha256.ToLowerInvariant()
    CreatorCoreSha256 = $previousHash
    CheckerCoreSha256 = $currentHash
    Jobs = 3
    Seconds = $campaignClock.Elapsed.TotalSeconds
    CheckOutput = $checked
    Scope = 'Real old-core registration, export, backup and history; new-core offline verification and independent byte/tree restore. No UI, task registration, runtime installation, other account, physical USB or downgrade validation.'
}
$evidencePath = Join-Path $buildRoot 'compatibility-evidence.json'
$evidence | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $evidencePath -Encoding utf8
& $Dotnet $newHarness --clean $fixture
if ($LASTEXITCODE -ne 0) { throw 'Owned compatibility fixture cleanup failed.' }
Write-Output "EVIDENCE=$evidencePath"
