param(
    [Parameter(Mandatory)][ValidateRange(1, 1000000)][int] $Count,
    [Parameter(Mandatory)][ValidateSet(1, 2)][int] $Levels
)

$ErrorActionPreference = 'Stop'
if (-not $IsWindows -or $env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
    throw 'This layout comparison requires a disposable GitHub-hosted Windows runner.'
}
$runnerRoot = (Resolve-Path -LiteralPath $env:RUNNER_TEMP).Path
$probe = Join-Path $runnerRoot 'FolderTwin-Layout-Build\CopyProbe.dll'
if (-not (Test-Path -LiteralPath $probe -PathType Leaf)) { throw 'Build CopyProbe first.' }
if ($runnerRoot.IndexOfAny([char[]]"`"`r`n") -ge 0) { throw 'Unexpected runner temp path.' }
$label = 'FTO' + [guid]::NewGuid().ToString('N').Substring(0, 8)
$work = Join-Path $runnerRoot "FolderTwin-Layout-$label"
New-Item -ItemType Directory -Path $work | Out-Null
$image = Join-Path $work 'objects.vhdx'
$creation = Join-Path $work 'create.txt'
$originalTemp = $env:TEMP
$originalTmp = $env:TMP

function Get-OwnedLayoutDisk {
    $attached = Get-DiskImage -ImagePath $image
    if (-not $attached.Attached -or $attached.ImagePath -ne $image) { throw 'The exact layout image is not attached.' }
    $disks = @($attached | Get-Disk)
    if ($disks.Count -ne 1 -or $disks[0].IsBoot -or $disks[0].IsSystem -or
        $disks[0].Size -ne 32GB -or $disks[0].BusType -ne 'File Backed Virtual') {
        throw 'The layout disk does not belong to the disposable image.'
    }
    return $disks[0]
}

try {
    Set-Content -LiteralPath $creation -Encoding ascii -Value "create vdisk file=`"$image`" maximum=32768 type=expandable"
    & (Join-Path ([Environment]::SystemDirectory) 'diskpart.exe') /s $creation
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $image -PathType Leaf)) { throw 'Image creation failed.' }
    Mount-DiskImage -ImagePath $image -NoDriveLetter | Out-Null
    $disk = Get-OwnedLayoutDisk
    if ($disk.PartitionStyle -ne 'RAW' -or $disk.NumberOfPartitions -ne 0) { throw 'Expected a new empty image.' }
    Initialize-Disk -InputObject $disk -PartitionStyle MBR | Out-Null
    $partition = New-Partition -InputObject (Get-OwnedLayoutDisk) -UseMaximumSize -AssignDriveLetter
    if ($partition.DiskNumber -ne (Get-OwnedLayoutDisk).Number -or $partition.IsBoot -or $partition.IsSystem) {
        throw 'The partition does not belong to the layout image.'
    }
    $letter = [string]$partition.DriveLetter
    if ($letter -cnotmatch '^[D-Z]$') { throw 'Unexpected disposable drive letter.' }
    $volume = Format-Volume -Partition $partition -FileSystem NTFS -NewFileSystemLabel $label -Confirm:$false
    if ($volume.FileSystemLabel -ne $label -or $volume.DriveLetter -ne $letter) { throw 'Unexpected layout volume.' }
    $scratch = New-Item -ItemType Directory -Path (Join-Path "${letter}:\" 'Temp')
    $env:TEMP = $scratch.FullName
    $env:TMP = $scratch.FullName
    $env:FOLDERTWIN_LAYOUT_VOLUME = $label
    dotnet $probe --object-layout-volume $Count $Levels
    if ($LASTEXITCODE -ne 0) { throw "The layout probe failed ($LASTEXITCODE)." }
}
finally {
    $env:TEMP = $originalTemp
    $env:TMP = $originalTmp
    Remove-Item Env:FOLDERTWIN_LAYOUT_VOLUME -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $image -PathType Leaf) {
        if ((Get-DiskImage -ImagePath $image).Attached) { Dismount-DiskImage -ImagePath $image | Out-Null }
        if ((Get-DiskImage -ImagePath $image).Attached) { throw 'The layout image remains attached.' }
        Remove-Item -LiteralPath $image
    }
    if (Test-Path -LiteralPath $creation -PathType Leaf) { Remove-Item -LiteralPath $creation }
    [IO.Directory]::Delete($work)
}
