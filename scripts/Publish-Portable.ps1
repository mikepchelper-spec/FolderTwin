#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$OutputDirectory,
    [string]$DotNet = 'dotnet',
    [string]$SourceArchive,
    [string]$SourceRevision
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$output = [IO.Path]::TrimEndingDirectorySeparator($ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory))
$sourceFile = if ($SourceArchive) { (Resolve-Path -LiteralPath $SourceArchive).Path }
$work = $output + '.build'
$runtimeVersion = '10.0.14'
if (-not $SourceRevision) {
    $SourceRevision = & git -C $repo rev-parse HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Indica SourceRevision al compilar un árbol sin Git.' }
}
if ($SourceRevision -cnotmatch '^[0-9a-f]{40}$') { throw 'SourceRevision debe ser el commit completo en minúsculas.' }
if (-not $sourceFile) {
    $head = & git -C $repo rev-parse HEAD
    $dirty = & git -C $repo status --porcelain
    if ($LASTEXITCODE -ne 0 -or $head -ne $SourceRevision -or $dirty) {
        throw 'La distribución necesita fuentes archivadas o un repositorio limpio del commit indicado.'
    }
}
if ($output.Equals($repo, [StringComparison]::OrdinalIgnoreCase) -or
    $output.StartsWith($repo + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Elige una ruta de salida fuera del repositorio.'
}
if ((Test-Path -LiteralPath $output) -or (Test-Path -LiteralPath $work)) {
    throw 'Las carpetas de salida y compilación deben ser nuevas.'
}

Push-Location -LiteralPath $repo
try {
    & $DotNet publish 'experiments/CopyLab/CopyLab.csproj' --configuration Release --runtime win-x64 --self-contained true `
        --artifacts-path $work --output $output --disable-build-servers `
        "-p:RuntimeFrameworkVersion=$runtimeVersion" "-p:PathMap=$repo=/_/FolderTwin" `
        -p:DebugType=None -p:DebugSymbols=false -p:ContinuousIntegrationBuild=true -p:IncludeSourceRevisionInInformationalVersion=false
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish terminó con código $LASTEXITCODE." }
    # The apphost continues to locate CopyLab.dll; the public entry point uses the product name.
    Copy-Item -LiteralPath (Join-Path $output 'CopyLab.exe') -Destination (Join-Path $output 'FolderTwin.exe')

    $cache = & $DotNet nuget locals global-packages --list --force-english-output
    if ($LASTEXITCODE -ne 0 -or $cache -isnot [string] -or -not $cache.StartsWith('global-packages: ')) {
        throw 'No se pudo localizar la caché de paquetes de .NET.'
    }
    $cache = $cache.Substring('global-packages: '.Length).Trim()
    $core = Join-Path $cache "microsoft.netcore.app.runtime.win-x64/$runtimeVersion"
    $desktop = Join-Path $cache "microsoft.windowsdesktop.app.runtime.win-x64/$runtimeVersion"
    Copy-Item -LiteralPath (Join-Path $repo 'LICENSE') -Destination $output
    Copy-Item -LiteralPath (Join-Path $core 'LICENSE.TXT') -Destination (Join-Path $output 'LICENSE-dotnet.txt')
    Copy-Item -LiteralPath (Join-Path $core 'THIRD-PARTY-NOTICES.TXT') -Destination (Join-Path $output 'THIRD-PARTY-NOTICES-dotnet.txt')
    Copy-Item -LiteralPath (Join-Path $desktop 'LICENSE') -Destination (Join-Path $output 'LICENSE-dotnet-desktop.txt')
    foreach ($guide in @('GUIA.md', 'NOTAS.md', 'PILOTO.md', 'COMPATIBILIDAD.md', 'INSTALACION.md', 'global.json')) {
        Copy-Item -LiteralPath (Join-Path $repo $guide) -Destination $output
    }
    Copy-Item -LiteralPath (Join-Path $repo 'distribucion/README.md') -Destination (Join-Path $output 'README.md')
    $probeDocs = Join-Path $output 'experiments/CopyProbe'
    New-Item -ItemType Directory -Path $probeDocs | Out-Null
    Get-ChildItem -LiteralPath (Join-Path $repo 'experiments/CopyProbe') -File -Filter '*.md' | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $probeDocs
    }
    if (-not $sourceFile) {
        $sourceFile = Join-Path $work 'FolderTwin-source.tar'
        & git -C $repo archive --format=tar "--output=$sourceFile" $SourceRevision
        if ($LASTEXITCODE -ne 0) { throw 'No se pudieron archivar las fuentes del paquete.' }
    }
    Copy-Item -LiteralPath $sourceFile -Destination (Join-Path $output 'FolderTwin-source.tar')

    # Usage guides stay offline. Development reports remain online, outside the frozen payload.
    foreach ($document in Get-ChildItem -LiteralPath $output -File -Recurse -Filter '*.md') {
        $content = [IO.File]::ReadAllText($document.FullName)
        $content = [regex]::Replace($content, '\]\(([^)\s]+)\)', {
            param($match)
            $link = $match.Groups[1].Value
            if ($link -match '^(?:[a-zA-Z]+:|#)') { return $match.Value }
            $parts = $link.Split('#', 2)
            $target = [IO.Path]::GetFullPath((Join-Path $document.DirectoryName $parts[0]))
            if (Test-Path -LiteralPath $target) { return $match.Value }
            $relative = [IO.Path]::GetRelativePath($output, $target).Replace('\', '/')
            if ($relative.StartsWith('../')) { throw "Enlace fuera del paquete: $link" }
            $anchor = if ($parts.Length -gt 1) { '#' + $parts[1] } else { '' }
            return "](https://github.com/mikepchelper-spec/FolderTwin/blob/$SourceRevision/$relative$anchor)"
        })
        [IO.File]::WriteAllText($document.FullName, $content)
    }
    $identity = [ordered]@{
        schemaVersion = 1
        sourceRevision = $SourceRevision
        runtimeVersion = $runtimeVersion
        sourceArchiveSha256 = if ($sourceFile) { (Get-FileHash -LiteralPath $sourceFile -Algorithm SHA256).Hash.ToLowerInvariant() } else { $null }
        acceptance = 'external'
    }
    [IO.File]::WriteAllText((Join-Path $output 'COMPILACION.json'), ($identity | ConvertTo-Json) + "`n")

    [string[]]$paths = Get-ChildItem -LiteralPath $output -File -Recurse -Force | ForEach-Object {
        $_.FullName.Substring($output.Length + 1).Replace('\', '/')
    }
    [Array]::Sort($paths, [StringComparer]::Ordinal)
    [string[]]$checksums = foreach ($path in $paths) {
        $hash = (Get-FileHash -LiteralPath (Join-Path $output $path) -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  $path"
    }
    [IO.File]::WriteAllText((Join-Path $output 'SHA256SUMS.txt'), ($checksums -join "`n") + "`n")
    Write-Host "Paquete portable de FolderTwin en $output"
}
finally {
    Pop-Location
}
