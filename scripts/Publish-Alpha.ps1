#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$OutputDirectory,
    [string]$DotNet = 'dotnet',
    [string]$SourceArchive
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$output = [IO.Path]::TrimEndingDirectorySeparator($ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory))
$sourceFile = if ($SourceArchive) { (Resolve-Path -LiteralPath $SourceArchive).Path }
$work = $output + '.build'
$runtimeVersion = '10.0.14'
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
    Copy-Item -LiteralPath (Join-Path $repo 'LICENSE') -Destination (Join-Path $output 'LICENSE-FolderTwin.txt')
    Copy-Item -LiteralPath (Join-Path $core 'LICENSE.TXT') -Destination (Join-Path $output 'LICENSE-dotnet.txt')
    Copy-Item -LiteralPath (Join-Path $core 'THIRD-PARTY-NOTICES.TXT') -Destination (Join-Path $output 'THIRD-PARTY-NOTICES-dotnet.txt')
    Copy-Item -LiteralPath (Join-Path $desktop 'LICENSE') -Destination (Join-Path $output 'LICENSE-dotnet-desktop.txt')
    foreach ($guide in @('README.md', 'V1.md', 'ALFA.md', 'PILOTO.md', 'ACEPTACION.md', 'PLAN.md', 'INVESTIGACION.md', 'COMPATIBILIDAD.md', 'INSTALACION.md')) {
        Copy-Item -LiteralPath (Join-Path $repo $guide) -Destination $output
    }
    $probeDocs = Join-Path $output 'experiments/CopyProbe'
    New-Item -ItemType Directory -Path $probeDocs | Out-Null
    Get-ChildItem -LiteralPath (Join-Path $repo 'experiments/CopyProbe') -File -Filter '*.md' | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $probeDocs
    }
    $interfaceDocs = Join-Path $output 'experiments/CopyLab'
    New-Item -ItemType Directory -Path $interfaceDocs | Out-Null
    Copy-Item -LiteralPath (Join-Path $repo 'experiments/CopyLab/INTERFAZ.md') -Destination $interfaceDocs
    if ($sourceFile) {
        Copy-Item -LiteralPath $sourceFile -Destination (Join-Path $output 'FolderTwin-source.tar')
    }

    [string[]]$paths = Get-ChildItem -LiteralPath $output -File -Recurse -Force | ForEach-Object {
        $_.FullName.Substring($output.Length + 1).Replace('\', '/')
    }
    [Array]::Sort($paths, [StringComparer]::Ordinal)
    [string[]]$checksums = foreach ($path in $paths) {
        $hash = (Get-FileHash -LiteralPath (Join-Path $output $path) -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  $path"
    }
    [IO.File]::WriteAllText((Join-Path $output 'SHA256SUMS.txt'), ($checksums -join "`n") + "`n")
    Write-Host "Compilación portable en $output"
}
finally {
    Pop-Location
}
