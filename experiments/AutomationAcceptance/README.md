# Ensayos complementarios de automatización

Los ensayos enlazan `CopyProbe.dll` de una extracción verificada y usan carpetas sintéticas nuevas en C: y D:. Requieren Windows 11 x64, dos volúmenes fijos NTFS y el SDK del proyecto. Verifica antes el paquete con `scripts/Test-Portable.ps1`. Usa el mismo paquete para `CopyProbe.dll` y `FolderTwin.exe`.

```powershell
$package = 'C:\FolderTwin-Distribucion\FolderTwin-6f782af'
$artifacts = Join-Path $env:TEMP ('FolderTwin-Acceptance-' + [guid]::NewGuid().ToString('N'))
dotnet build experiments/AutomationAcceptance --artifacts-path $artifacts "-p:ReferenceCoreAssembly=$package\CopyProbe.dll"
$harness = Join-Path $artifacts 'bin/AutomationAcceptance/debug/CopyLab.dll'
dotnet $harness --drive-aliases
dotnet $harness --waiting-controls "$package\FolderTwin.exe"
dotnet $harness --daily-performance "$package\FolderTwin.exe" 4096 ProtectedMirror prepared
dotnet $harness --storage-growth "$package\FolderTwin.exe" 4096
dotnet $harness --storage-safety "$package\FolderTwin.exe"
```

`--drive-aliases` registra los tres modos desde una letra temporal que apunta al destino de D:. Cambia el alias, reutiliza la letra original para un sustituto en C: y comprueba identidades, ausencia, retorno y restauración. Retira sus propios alias al finalizar. El ensayo trabaja con alias de la sesión y deja la reasignación de una partición VHDX al laboratorio dedicado.

`--waiting-controls` ejecuta el monitor del paquete y aparta temporalmente cada destino sintético de D:. Comprueba pausa, desactivación y retirada antes de devolver el destino. Exige reactivación explícita para respaldar y comprueba los bytes y el historial. Usa un registro nuevo y un monitor sin tarea nativa. La aceptación del Programador se ejecuta aparte.

El mismo ensayo traslada al origen un árbol preparado fuera del alcance del vigilante con 256 archivos y renombra después su carpeta raíz. Recupera todos los descendientes con el origen apartado. Acumulativo debe conservar las dos rutas, versionado debe guardar el cambio de ruta y espejo debe conservar el árbol previo y pedir revisión ante el renombrado masivo.

`--daily-performance` acepta 4.096 o 329.000 archivos y un modo (`Accumulative`, `ProtectedMirror` o `Versions`). Ejecuta el monitor del paquete sobre un registro aislado y mide cinco operaciones ordinarias y la recuperación del archivo afectado con el origen apartado. `prepared` reutiliza la preparación verificada del arnés del motor para medir un árbol ya poblado en acumulativo o espejo. En esos modos, la escala de 329.000 usa siempre esa preparación. Evita ejecutar otras pruebas de disco simultáneamente si vas a comparar las latencias.

`--storage-growth` usa el monitor extraído y mide por fase el tamaño lógico y la asignación de archivos NTFS. La variante de 4.096 abre los tres trabajos a la vez. La de 329.000 concentra el ensayo en versionado. Revisa la salida por fase y el SHA-256 de la restauración del último guardado. `--storage-safety` usa tres trabajos nuevos con un presupuesto inyectado solo para la comprobación inicial y un fallo de disco lleno durante la publicación. Comprueba recuperación sin origen, continuidad de otro trabajo, reanudación y asignación liberada por la purga manual. La candidata de correcciones comprueba además la retirada de metadatos expirados en los tres modos, restauración del punto retenido sin origen y la siguiente copia automática. `FolderTwin --product-retention-smoke` ejecuta por separado el recorrido WPF afectado. El [informe de capacidad](../../CAPACIDAD.md) registra el alcance y los resultados.

`Test-IsolatedStartup.ps1 -PackageDirectory <extracción> -SourceRevision <commit-completo>` verifica el paquete y abre su `FolderTwin.exe` sin argumentos. Antes comprueba en un proceso aparte que Windows devuelve la carpeta temporal al consultar `LocalAppData` con el entorno aislado. Exige que Ruta aparezca, que el registro se cree en esa carpeta y que la ventana cierre con el registro vacío. Conserva el resultado en el espacio desechable. Este ensayo usa la cuenta interactiva actual y un perfil de aplicación aislado por proceso. El arnés del runner hospedado permite comprobar por separado una cuenta Windows desechable completa.

`--first-use-update <paquete-anterior> <paquete-nuevo>` requiere dos extracciones verificadas y que la versión anterior ya esté en la ubicación estable de la sesión interactiva. Compila el arnés con el núcleo del paquete nuevo. El ensayo registra una tarea nativa para un registro sintético, confirma un punto con la versión anterior, actualiza con el mismo flujo de la interfaz y exige que el monitor nuevo confirme un segundo punto. Después retira la tarea y verifica ambos puntos con el origen apartado. Usa `--withdraw-new` solo en un entorno desechable o si la instalación nueva está ausente y nadie más la usa. El ensayo exige que ese directorio aún no exista antes de instalar y, en ese caso, comprueba también su retirada revisada y la conservación de la versión anterior.

`Test-VolumeCandidate.ps1` ejecuta el laboratorio de capacidad y cambio de letra sobre un ZIP existente. Comprueba el SHA-256 del ZIP, el manifiesto, el inventario, las fuentes y el commit. Extrae de las fuentes verificadas el guion del laboratorio. En un runner Windows hospedado desechable exige que `%RUNNER_TEMP%\FolderTwin-volume-package` todavía no exista. `-VerifyOnly` prepara y comprueba la extracción local sin montar discos.

```powershell
./experiments/AutomationAcceptance/Test-VolumeCandidate.ps1 `
  -PackageZip 'C:\FolderTwin-Distribucion\FolderTwin-win-x64-598fbc7.zip' `
  -ZipSha256 '05c58508b888f05b041875614821f42c68de0c5a0f967ab0d00b145fd696a084' `
  -ManifestSha256 '700bfb6c647d621849963f35534e8eb5abb6d339e0e9554cf842f981f2eefe6e' `
  -SourceRevision '598fbc710e4b29ecd52b76185eda76911da969eb' -VerifyOnly
```

Para la prueba nativa, ejecuta el mismo comando sin `-VerifyOnly` en el runner y usa la ruta del ZIP disponible allí. Conserva la salida junto al informe posterior de la candidata.

`--cloud-recovery-workers <FolderTwin.exe>` prueba credenciales protegidas, fallo del ejecutor real, cancelación desde otro registro y un respaldo local simultáneo. Comprueba también el rechazo de salida ocupada en el ejecutor de producto del ZIP. `--cloud-permissions <FolderTwin.exe>` compara la escritura en la pasarela rclone habilitada con su rechazo en solo lectura y verifica que la recuperación conserva los hashes del repositorio sintético. Ambos requieren Kopia y rclone fijados en la extracción y funcionan en un único volumen local NTFS. [Evidencia de la candidata](../../ENTREGA-CORRECCIONES-AUDITORIA.md)

`--cloud-history <FolderTwin.exe>` entrega 10.000 fechas sintéticas por stdout al adaptador del ZIP. Valida recuento y orden y mide tiempo y asignaciones administradas del lector. No mide latencia de proveedor ni memoria máxima del proceso.
