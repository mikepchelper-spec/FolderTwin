# Ensayos complementarios de automatización

Los ensayos de `tests/AutomationAcceptance` enlazan `FolderTwin.Core.dll` de una extracción verificada y operan exclusivamente sobre carpetas temporales desechables (`%TEMP%`, o un segundo volumen desechable explícito mediante `FOLDERTWIN_SECOND_VOLUME_ROOT`). Requieren Windows 11 x64 y el SDK fijado en [global.json](../../global.json). Verifica antes el paquete con `scripts/Test-Portable.ps1`.

```powershell
$package = "$env:TEMP\FolderTwin-win-x64"
$artifacts = Join-Path $env:TEMP ('FolderTwin-Acceptance-' + [guid]::NewGuid().ToString('N'))
dotnet build tests/AutomationAcceptance -c Release --artifacts-path $artifacts "-p:ReferenceCoreAssembly=$package\FolderTwin.Core.dll"
$harness = Join-Path $artifacts 'bin/AutomationAcceptance/release/AutomationAcceptance.dll'

dotnet $harness --packaged-binary-audit "$package\FolderTwin.exe"
dotnet $harness --drive-aliases
dotnet $harness --waiting-controls "$package\FolderTwin.exe"
dotnet $harness --daily-performance "$package\FolderTwin.exe" 4096 ProtectedMirror prepared
dotnet $harness --storage-growth "$package\FolderTwin.exe" 4096
dotnet $harness --storage-safety "$package\FolderTwin.exe"
```

- `--packaged-binary-audit`: comprueba que el paquete no incluya ensamblados de prueba (`CopyLab*`, `CopyProbe*`), que `FolderTwin.dll` y `FolderTwin.Core.dll` no contengan tipos `*Probe` ni `*Smoke`, y que `FolderTwin.exe` rechace banderas CLI de prueba con código `2`.
- `--drive-aliases`: registra los tres modos a través de letras DOS temporales por sesión (`DefineDosDevice`), cambia el alias, reutiliza la letra original para un sustituto y comprueba identidades NTFS, ausencia, retorno y restauración sin origen. Si defines `FOLDERTWIN_SECOND_VOLUME_ROOT` apuntando a un volumen fijo desechable, valida además volúmenes distintos.
- `--waiting-controls`: ejecuta el monitor del paquete, aparta temporalmente cada destino sintético, comprueba pausa/desactivación/retirada, traslada un árbol de 256 archivos y verifica renombrados y restauración offline.
- `--daily-performance` y `--storage-growth`: miden latencia de detección/confirmación y huella lógica/asignada NTFS en 4.096 o 329.000 archivos sintéticos. Consulta [HISTORIAL.md](../../HISTORIAL.md).
- `--storage-safety`: inyecta presupuestos de reserva y fallos `0x80070070` de disco lleno durante publicación, verificando conservación del punto previo, reanudación y purga de metadatos expirados.
- `Test-IsolatedStartup.ps1 -PackageDirectory <extracción> -SourceRevision <commit-completo>`: verifica el paquete y lanza `FolderTwin.exe` sin argumentos bajo un perfil `LocalAppData` aislado en `%TEMP%`, comprobando la apertura de **Ruta** y el cierre limpio sin crear trabajos espurios.
- `--first-use-update <paquete-anterior> <paquete-nuevo>`: verifica en entorno desechable el traspaso real de la tarea del monitor entre versiones instaladas (incluidas entregas anteriores basadas en `CopyLab.exe` y entregas actuales basadas en `FolderTwin.exe`) y la restauración posterior de ambos puntos.
- `--cloud-faults`, `--cloud-recovery-workers`, `--cloud-permissions`, `--cloud-history` y `--cloud-scale`: validan sobre servicios loopback desechables la tolerancia a fallos HTTP/transporte, trabajadores de recuperación remota en segundo plano, pasarela `rclone` de solo lectura y escalado con Kopia. Consulta [NUBE.md](../../NUBE.md).
