#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PackageDirectory,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$SourceRevision
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $PackageDirectory).Path
$identity = Get-Content -LiteralPath (Join-Path $root 'COMPILACION.json') -Raw | ConvertFrom-Json
if ($identity.schemaVersion -ne 1 -or $identity.sourceRevision -cne $SourceRevision -or
    $identity.acceptance -cne 'external' -or $identity.runtimeVersion -cne '10.0.14' -or
    $identity.sourceArchiveSha256 -cne (Get-FileHash -LiteralPath (Join-Path $root 'FolderTwin-source.tar')).Hash.ToLowerInvariant()) {
    throw 'La identidad del paquete no coincide con las fuentes y runtime declarados.'
}
$expected = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($line in Get-Content -LiteralPath (Join-Path $root 'SHA256SUMS.txt')) {
    if ($line -cnotmatch '^([0-9a-f]{64})  ([^\\]+)$') { throw 'Línea de manifiesto inválida.' }
    $hash = $Matches[1]; $relative = $Matches[2]
    if ($relative.StartsWith('/') -or ($relative.Split('/') | Where-Object { $_ -in @('', '.', '..') }) -or
        $relative.Contains(':') -or $relative -ceq 'SHA256SUMS.txt' -or -not $expected.Add($relative)) {
        throw "Ruta de manifiesto insegura o duplicada: $relative"
    }
    $file = Join-Path $root $relative
    if ((Get-FileHash -LiteralPath $file).Hash.ToLowerInvariant() -cne $hash) { throw "Hash distinto: $relative" }
}
$actual = @(Get-ChildItem -LiteralPath $root -File -Recurse -Force | ForEach-Object {
    [IO.Path]::GetRelativePath($root, $_.FullName).Replace('\', '/')
} | Where-Object { $_ -cne 'SHA256SUMS.txt' })
if (-not $expected.SetEquals([string[]]$actual)) { throw 'El conjunto de archivos no coincide con el manifiesto.' }
foreach ($required in @('README.md', 'NOTAS.md', 'GUIA.md', 'SUPPORT.md', 'INSTALACION.md', 'COMPATIBILIDAD.md', 'PILOTO.md',
    'LICENSE', 'LICENSE-dotnet.txt', 'LICENSE-dotnet-desktop.txt', 'THIRD-PARTY-NOTICES-dotnet.txt', 'FolderTwin.exe')) {
    if (-not $expected.Contains($required)) { throw "Falta un componente obligatorio: $required" }
}
foreach ($report in @('ENTREGA.md', 'V1.md', 'PLAN.md', 'ALFA.md', 'ACEPTACION.md', 'INVESTIGACION.md')) {
    if (Test-Path -LiteralPath (Join-Path $root $report)) { throw "Informe de desarrollo mezclado con las guías: $report" }
}
$readme = Get-Content -LiteralPath (Join-Path $root 'README.md') -Raw
if (-not $readme.Contains('COMPILACION.json') -or -not $readme.Contains('después') -or -not $readme.Contains('/releases')) {
    throw 'El paquete no separa su identidad de la aceptación posterior.'
}
foreach ($document in Get-ChildItem -LiteralPath $root -File -Recurse -Filter '*.md') {
    foreach ($match in [regex]::Matches([IO.File]::ReadAllText($document.FullName), '\]\(([^)\s]+)\)')) {
        $link = $match.Groups[1].Value
        if ($link -match '^(?:[a-zA-Z]+:|#)') { continue }
        if (-not (Test-Path -LiteralPath (Join-Path $document.DirectoryName $link.Split('#', 2)[0]))) {
            throw "Enlace local sin destino en $($document.Name): $link"
        }
    }
}
Write-Host "PASS package identity, source archive, $($expected.Count) file hashes, exact inventory, licenses, guides and separate post-build acceptance."
