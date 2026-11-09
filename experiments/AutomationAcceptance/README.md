# Ensayos complementarios de automatización

Los dos ensayos enlazan `CopyProbe.dll` de una extracción verificada y usan carpetas sintéticas nuevas en C: y D:. Requieren Windows 11 x64, dos volúmenes fijos NTFS y el SDK del proyecto. Verifica antes el paquete con `scripts/Test-Portable.ps1`. Usa el mismo paquete para `CopyProbe.dll` y `FolderTwin.exe`.

```powershell
$package = 'C:\FolderTwin-Distribucion\FolderTwin-3cb9c6a'
$artifacts = Join-Path $env:TEMP ('FolderTwin-Acceptance-' + [guid]::NewGuid().ToString('N'))
dotnet build experiments/AutomationAcceptance --artifacts-path $artifacts "-p:ReferenceCoreAssembly=$package\CopyProbe.dll"
$harness = Join-Path $artifacts 'bin/AutomationAcceptance/debug/CopyLab.dll'
dotnet $harness --drive-aliases
dotnet $harness --waiting-controls "$package\FolderTwin.exe"
```

`--drive-aliases` registra los tres modos desde una letra temporal que apunta al destino de D:. Cambia el alias, reutiliza la letra original para un sustituto en C: y comprueba identidades, ausencia, retorno y restauración. Retira sus propios alias al finalizar. El ensayo trabaja con alias de la sesión y deja la reasignación de una partición VHDX al laboratorio dedicado.

`--waiting-controls` ejecuta el monitor del paquete y aparta temporalmente cada destino sintético de D:. Comprueba pausa, desactivación y retirada antes de devolver el destino. Exige reactivación explícita para respaldar y comprueba los bytes y el historial. Usa un registro nuevo y un monitor sin tarea nativa. La aceptación del Programador se ejecuta aparte.
