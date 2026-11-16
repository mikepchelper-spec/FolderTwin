# Conservación de Zone.Identifier: siguiente hito

A14-07 sigue pendiente. El motor actual rechaza los flujos alternativos. El hito debe conservar los bytes de `Zone.Identifier` y verificar su recuperación local y remota antes de admitirlos en el producto. El origen permanece de solo lectura y los demás flujos mantienen su rechazo.

## Recorrido nativo que debe probarse primero

`BackupRead`, `BackupSeek` y `BackupWrite` son candidatos para operar sobre el handle ya comprobado, sin volver a resolver el nombre de la ruta. Requieren handles síncronos y contexto propio que debe liberarse al terminar. `BackupSeek` permite saltar los datos del flujo principal dentro del mismo subflujo. No se debe usar un nombre de ADS construido desde una ruta que pueda cambiar mientras se copia. Tampoco se debe suponer que `NtCreateFile` admite un handle de archivo como raíz de una ruta relativa: su contrato documenta una raíz de directorio.

El primer ensayo, exclusivamente sintético, debe demostrar lectura/escritura de la marca con los permisos y el modo de compartición actuales, preservación del contenido principal y de la posición del handle y liberación del contexto tras error. Una lectura en bloques acotados debe interpretar los encabezados de `WIN32_STREAM_ID` y saltar contenidos grandes mediante `BackupSeek`. No debe requerir privilegios de respaldo, restaurar ACL ni ampliar los permisos de origen para conseguirlo. Antes de integrar hay que resolver con esa prueba cualquier límite de la API o de sus buffers.

Fuentes primarias consultadas: [BackupRead](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-backupread), [BackupWrite](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-backupwrite), [BackupSeek](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-backupseek), [WIN32_STREAM_ID](https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-win32_stream_id), [NtCreateFile](https://learn.microsoft.com/en-us/windows/win32/api/winternl/nf-winternl-ntcreatefile) y [nombres de flujos NTFS](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-fscc/c54dec26-1551-4d3a-a0ea-4fa40f848eb3).

## Contrato de datos propuesto

- Admitir solo la marca del archivo, con un máximo explícito inicial de 16 KiB. Distinguir ausencia de flujo y flujo vacío. Conservar bytes, incluyendo codificación y saltos; no interpretar ni normalizar su contenido.
- Guardar el valor codificado y SHA-256 como metadatos de cada ruta. Extender el catálogo mediante una versión nueva que mantenga lectura de las anteriores y sus validaciones de tamaño. La igualdad de metadatos debe comparar valores y detectar un cambio exclusivo de la marca.
- Mantener los objetos CAS identificados por los bytes principales. Dos archivos con los mismos bytes y marcas diferentes comparten objeto, conservando metadatos independientes. Los archivos internos de control y objetos conservan su validación estricta de flujos.
- Escribir la marca en el temporal de datos antes de su flush y promoción. Verificar marca y contenido antes de confirmar; mantener esa verificación en restauración y reanudación. Comparar de nuevo la marca de origen para detectar cambios observados durante la copia. Esto no sustituye una instantánea VSS.
- No mostrar ni registrar el contenido de la marca: puede contener URL de origen y referencia. Las pruebas usarán dominios y rutas inventados.

## Transporte remoto

No se da por supuesto que la versión fijada de Kopia conserve ADS. Su [incidencia de documentación de metadatos](https://github.com/kopia/kopia/issues/3587) y las discusiones históricas no acreditan ese comportamiento en 0.23.1. La siguiente prueba debe crear una instantánea con esa versión y reabrirla desde una conexión de lectura y caché vacía, apartando los datos originales, para comparar el flujo restaurado con una lectura independiente.

Si la prueba no conserva la marca, el transporte necesita un portador explícito y versionado de metadatos dentro del repositorio cifrado. Reutilizar el catálogo y su lector cuando sea posible. La versión remota debe distinguir esos puntos para que un lector antiguo no ofrezca una recuperación aparentemente completa que omita la marca. La propuesta es usar etiquetas nuevas para los puntos de ese formato, mantener lectura de los puntos antiguos y comprobar el inventario del portador además del contenido. La estructura final se decide después de probar Kopia; no hay un cambio de formato remoto implementado todavía.

La elección debe servir también a A14-08: descargar primero el catálogo, permitir seleccionar archivo/carpeta y verificar solo el contenido solicitado, con seguimiento persistente, salida parcial identificada y cancelación. Evitar un segundo catálogo autoritativo o una reescritura del motor. Documentar también el procedimiento independiente con Kopia para cada formato.

## Aceptación antes de habilitar

1. Flujo vacío, bytes arbitrarios, límite exacto y exceso, modificación exclusiva de la marca y lectura ocupada. Otros ADS, EAs, reparse points y enlaces duros siguen rechazados.
2. Dos rutas con contenido idéntico y marcas distintas, y otra sin marca. Copia, actualización, verificación, interrupción, reanudación, cuarentena/deshacer y restauración sin origen en los tres modos.
3. Lectores anteriores de catálogos, marca/hash dañados y entradas semánticamente inválidas con checksum recalculado. No promover ni anunciar recuperación completa si falla un metadato requerido.
4. Exportación local, subida, pérdida de confirmación, conexión independiente, restauración completa y seleccionada y comparación independiente del flujo. Repetir sobre S3/rclone de loopback con herramientas fijadas por hash.
5. Ayuda y límites actualizados, sin presentar los resultados sintéticos como prueba de SmartScreen, Office, proveedores reales o durabilidad física. El paquete final del goal incluirá estas regresiones desde su extracción.
