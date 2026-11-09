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

El mismo ensayo traslada al origen un árbol preparado fuera del alcance del vigilante con 256 archivos y renombra después su carpeta raíz. Recupera todos los descendientes con el origen apartado. Acumulativo debe conservar las dos rutas, versionado debe guardar el cambio de ruta y espejo debe conservar el árbol previo y pedir revisión ante el renombrado masivo.

`Test-IsolatedStartup.ps1 -PackageDirectory <extracción> -SourceRevision <commit-completo>` verifica el paquete y abre su `FolderTwin.exe` sin argumentos. Antes comprueba en un proceso aparte que Windows devuelve la carpeta temporal al consultar `LocalAppData` con el entorno aislado. Exige que Ruta aparezca, que el registro se cree en esa carpeta y que la ventana cierre con el registro vacío. Conserva el resultado en el espacio desechable. Este ensayo usa la cuenta interactiva actual y un perfil de aplicación aislado por proceso. El arnés del runner hospedado permite comprobar por separado una cuenta Windows desechable completa.

`Test-VolumeCandidate.ps1` ejecuta el laboratorio de capacidad y cambio de letra sobre un ZIP existente. Comprueba el SHA-256 del ZIP, el manifiesto, el inventario, las fuentes y el commit. Extrae de las fuentes verificadas el guion del laboratorio. En un runner Windows hospedado desechable exige que `%RUNNER_TEMP%\FolderTwin-volume-package` todavía no exista. `-VerifyOnly` prepara y comprueba la extracción local sin montar discos.

```powershell
./experiments/AutomationAcceptance/Test-VolumeCandidate.ps1 `
  -PackageZip 'C:\FolderTwin-Distribucion\FolderTwin-win-x64-598fbc7.zip' `
  -ZipSha256 '05c58508b888f05b041875614821f42c68de0c5a0f967ab0d00b145fd696a084' `
  -ManifestSha256 '700bfb6c647d621849963f35534e8eb5abb6d339e0e9554cf842f981f2eefe6e' `
  -SourceRevision '598fbc710e4b29ecd52b76185eda76911da969eb' -VerifyOnly
```

Para la prueba nativa, ejecuta el mismo comando sin `-VerifyOnly` en el runner y usa la ruta del ZIP disponible allí. Conserva la salida junto al informe posterior de la candidata.
