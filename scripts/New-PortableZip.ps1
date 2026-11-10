#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PackageDirectory,
    [Parameter(Mandatory)][string]$ZipPath,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$SourceRevision
)

$ErrorActionPreference = 'Stop'
$package = (Resolve-Path -LiteralPath $PackageDirectory).ProviderPath
$target = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ZipPath)
if ($target.StartsWith([IO.Path]::TrimEndingDirectorySeparator($package) + [IO.Path]::DirectorySeparatorChar,
    [StringComparison]::OrdinalIgnoreCase)) { throw 'Guarda el ZIP fuera de la carpeta del paquete.' }
if (Test-Path -LiteralPath $target) { throw 'El ZIP de salida debe ser nuevo.' }

& (Join-Path $PSScriptRoot 'Test-Portable.ps1') -PackageDirectory $package -SourceRevision $SourceRevision
[string[]]$paths = Get-ChildItem -LiteralPath $package -File -Recurse -Force | ForEach-Object {
    [IO.Path]::GetRelativePath($package, $_.FullName).Replace('\', '/')
}
[Array]::Sort($paths, [StringComparer]::Ordinal)
$timestamp = [DateTimeOffset]::new(2000, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
$temporary = $target + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
try {
    $output = [IO.FileStream]::new($temporary, [IO.FileMode]::CreateNew)
    try {
        $archive = [IO.Compression.ZipArchive]::new($output, [IO.Compression.ZipArchiveMode]::Create, $true)
        try {
            foreach ($path in $paths) {
                $entry = $archive.CreateEntry($path, [IO.Compression.CompressionLevel]::Optimal)
                $entry.LastWriteTime = $timestamp
                $entry.ExternalAttributes = 0
                $inputFile = [IO.File]::OpenRead((Join-Path $package $path))
                try {
                    $stream = $entry.Open()
                    try { $inputFile.CopyTo($stream) } finally { $stream.Dispose() }
                } finally { $inputFile.Dispose() }
            }
        } finally { $archive.Dispose() }
        $output.Flush($true)
    } finally { $output.Dispose() }
    [IO.File]::Move($temporary, $target)
} finally {
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary }
}
Get-FileHash -LiteralPath $target -Algorithm SHA256
