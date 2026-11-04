# Run only inside the dedicated GitHub-hosted Windows job.
$ErrorActionPreference = 'Stop'
if (-not $IsWindows -or $env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
    throw 'The volume lab requires a disposable GitHub-hosted Windows runner.'
}

$tempRoot = [IO.Path]::GetFullPath($env:RUNNER_TEMP)
$probe = Join-Path $tempRoot 'FolderTwin-Probe-Build\CopyProbe.dll'
if (-not (Test-Path -LiteralPath $probe -PathType Leaf)) { throw 'Build CopyProbe first.' }
if ($tempRoot.IndexOfAny([char[]]"`"`r`n") -ge 0) { throw 'Unexpected runner temp path.' }

$label = 'FTL' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$work = Join-Path $tempRoot "FolderTwin-Vhd-$label"
$image = Join-Path $work 'scratch.vhdx'
$createScript = Join-Path $work 'create.txt'
New-Item -ItemType Directory -Path $work | Out-Null

function Get-OwnedDisk {
    $attached = Get-DiskImage -ImagePath $image
    if (-not $attached.Attached -or $attached.ImagePath -ne $image) { throw 'Lab image is not attached.' }
    $disk = @($attached | Get-Disk)
    if ($disk.Count -ne 1 -or $disk[0].IsBoot -or $disk[0].IsSystem -or
        $disk[0].Size -ne 128MB -or $disk[0].BusType -ne 'File Backed Virtual') {
        throw 'Attached disk does not match the isolated VHDX.'
    }
    return $disk[0]
}

function Invoke-Probe([string] $phase) {
    dotnet $probe --volume-lab $phase
    if ($LASTEXITCODE -ne 0) { throw "Volume probe failed in phase $phase ($LASTEXITCODE)." }
}

try {
    # DiskPart only creates the new backing file. Partition and format by the
    # disk object obtained from that exact image, never by an input disk number.
    Set-Content -LiteralPath $createScript -Encoding ascii -Value "create vdisk file=`"$image`" maximum=128 type=fixed"
    & (Join-Path ([Environment]::SystemDirectory) 'diskpart.exe') /s $createScript
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $image -PathType Leaf)) { throw 'VHDX creation failed.' }

    Mount-DiskImage -ImagePath $image -NoDriveLetter | Out-Null
    $disk = Get-OwnedDisk
    if ($disk.PartitionStyle -ne 'RAW' -or $disk.NumberOfPartitions -ne 0) { throw 'Expected a new raw disk.' }
    Initialize-Disk -InputObject $disk -PartitionStyle MBR | Out-Null
    $disk = Get-OwnedDisk
    $partition = New-Partition -InputObject $disk -UseMaximumSize -AssignDriveLetter
    if ($partition.DiskNumber -ne (Get-OwnedDisk).Number -or $partition.IsBoot -or $partition.IsSystem) {
        throw 'Partition does not belong to the isolated VHDX.'
    }
    $letter = [string]$partition.DriveLetter
    if ($letter -cnotmatch '^[D-Z]$') { throw 'Unexpected disposable drive letter.' }
    $volume = Format-Volume -Partition $partition -FileSystem NTFS -NewFileSystemLabel $label -Confirm:$false
    if ($volume.FileSystemLabel -ne $label -or $volume.DriveLetter -ne $letter) { throw 'Unexpected formatted volume.' }

    $root = Join-Path "${letter}:\" $label
    New-Item -ItemType Directory -Path $root | Out-Null
    Set-Content -LiteralPath (Join-Path $root 'runner.txt') -Value $label -Encoding ascii
    $env:FOLDERTWIN_LAB_LABEL = $label
    $env:FOLDERTWIN_LAB_LETTER = $letter
    $env:FOLDERTWIN_LAB_STATE = $work
    Invoke-Probe 'fill'

    $used = @([IO.DriveInfo]::GetDrives() | ForEach-Object { $_.Name.Substring(0, 1) })
    $newLetter = 'Z', 'Y', 'X', 'W' | Where-Object { $_ -notin $used } | Select-Object -First 1
    if (-not $newLetter) { throw 'No spare drive letter on the hosted runner.' }
    $disk = Get-OwnedDisk
    $current = Get-Partition -DiskNumber $disk.Number -PartitionNumber $partition.PartitionNumber
    if ($current.DriveLetter -ne $letter -or $current.IsBoot -or $current.IsSystem -or
        ($current | Get-Volume).FileSystemLabel -ne $label) { throw 'Wrong partition before changing the letter.' }
    Set-Partition -InputObject $current -NewDriveLetter $newLetter
    $changed = Get-Partition -DiskNumber (Get-OwnedDisk).Number -PartitionNumber $partition.PartitionNumber
    if ($changed.DriveLetter -ne $newLetter -or [IO.Directory]::Exists($root)) { throw 'Drive letter did not change.' }
    Write-Output "The disposable volume moved from $letter to $newLetter."
    $env:FOLDERTWIN_LAB_LETTER = $newLetter
    $registeredMarker = [IO.File]::ReadAllBytes("${newLetter}:\$label\.foldertwin-destination")

    Dismount-DiskImage -ImagePath $image | Out-Null
    if ((Get-DiskImage -ImagePath $image).Attached -or [IO.Directory]::Exists("${newLetter}:\$label")) {
        throw 'The disposable volume did not detach.'
    }
    Invoke-Probe 'absent'

    # Reuse the letter for a synthetic directory on a different volume. The
    # alias is released before reattaching the image.
    $substitute = Join-Path $work 'substitute'
    $substituteRoot = Join-Path $substitute $label
    $substituteMarker = Join-Path $substituteRoot '.foldertwin-destination'
    $subst = Join-Path ([Environment]::SystemDirectory) 'subst.exe'
    $aliasCreated = $false
    try {
        [IO.Directory]::CreateDirectory($substituteRoot) | Out-Null
        [IO.File]::WriteAllBytes($substituteMarker, $registeredMarker)
        & $subst "${newLetter}:" $substitute
        if ($LASTEXITCODE -ne 0) { throw 'Could not assign the test substitute.' }
        $aliasCreated = $true
        Invoke-Probe 'substituted'
    }
    finally {
        if ($aliasCreated) {
            & $subst "${newLetter}:" /d
            if ($LASTEXITCODE -ne 0) { throw 'Could not release the test substitute.' }
        }
        if (Test-Path -LiteralPath $substituteMarker -PathType Leaf) { Remove-Item -LiteralPath $substituteMarker }
        if ([IO.Directory]::Exists($substituteRoot)) { [IO.Directory]::Delete($substituteRoot) }
        if ([IO.Directory]::Exists($substitute)) { [IO.Directory]::Delete($substitute) }
    }

    Mount-DiskImage -ImagePath $image -NoDriveLetter | Out-Null
    $remounted = Get-Partition -DiskNumber (Get-OwnedDisk).Number -PartitionNumber $partition.PartitionNumber
    if ($remounted.IsBoot -or $remounted.IsSystem -or ($remounted | Get-Volume).FileSystemLabel -ne $label) {
        throw 'Wrong partition after reattaching the disposable image.'
    }
    if ([int]$remounted.DriveLetter -eq 0) {
        Add-PartitionAccessPath -InputObject $remounted -AccessPath "${newLetter}:"
    } elseif ($remounted.DriveLetter -ne $newLetter) {
        Set-Partition -InputObject $remounted -NewDriveLetter $newLetter
    }
    $remounted = Get-Partition -DiskNumber (Get-OwnedDisk).Number -PartitionNumber $partition.PartitionNumber
    if ($remounted.DriveLetter -ne $newLetter) { throw 'Unexpected drive letter after reattaching.' }
    Invoke-Probe 'reopen'
}
finally {
    if (Test-Path -LiteralPath $image -PathType Leaf) {
        $attached = Get-DiskImage -ImagePath $image
        if ($attached.Attached) { Dismount-DiskImage -ImagePath $image | Out-Null }
        Remove-Item -LiteralPath $image
    }
    foreach ($path in @($createScript, (Join-Path $work 'destination.json'))) {
        if (Test-Path -LiteralPath $path -PathType Leaf) { Remove-Item -LiteralPath $path }
    }
    [IO.Directory]::Delete($work)
}
