# Conservación de Zone.Identifier

FolderTwin conserva los bytes de `Zone.Identifier` por ruta en los tres modos de respaldo local y en la recuperación remota de tercer nivel (`layout 2`). El origen permanece de solo lectura y los demás flujos alternativos NTFS mantienen su rechazo explícito. No acredita políticas específicas de SmartScreen u Office, cuentas reales de proveedores ni durabilidad física.

## Recorrido nativo

`ZoneIdentifierStream` usa `BackupRead`, `BackupSeek` y `BackupWrite` sobre el handle comprobado, sin volver a resolver el nombre. Mantiene la posición del flujo principal y libera el contexto también tras error. Lee bloques de 64 KiB, limita nombres y encabezados y salta el contenido principal con `BackupSeek`. No construye un nombre de ADS desde la ruta ni habilita privilegios ni restaura ACL.

`--zone-native` probó ausencia, flujo vacío, bytes arbitrarios, 16 KiB exactos, exceso, otros flujos, errores repetidos y reemplazo de una marca larga por otra corta o vacía. Conservó contenido principal y posición con origen de lectura compartida y salida exclusiva. El ensayo detectó que `BackupWrite` puede crear un flujo desde un handle base de lectura: el adaptador exige además `FILE_WRITE_DATA`, comprobado mediante `FileAccessInformation`, antes de entrar en esa API.

Fuentes primarias del contrato nativo: [BackupRead](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-backupread), [BackupWrite](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-backupwrite), [BackupSeek](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-backupseek), [WIN32_STREAM_ID](https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-win32_stream_id) y [nombres de flujos NTFS](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-fscc/c54dec26-1551-4d3a-a0ea-4fa40f848eb3).

## Contrato de datos

- Solo admite la marca del archivo, con un máximo de 16 KiB. Distingue ausencia y flujo vacío. Conserva bytes, codificación y saltos sin interpretarlos ni normalizarlos.
- El catálogo v4 añade `zoneData` base64 canónico y `zoneHash` SHA-256 hexadecimal minúsculo. Ambos son obligatorios y ambos son null cuando no hay marca. Mantiene lectura de v1/v2/v3. Un cambio exclusivo de la marca cambia los metadatos del punto. Los puntos sin marcas siguen escribiéndose como v3.
- Los objetos CAS se identifican por los bytes principales. Dos rutas con bytes iguales y marcas distintas comparten objeto y conservan metadatos independientes. Los objetos y archivos de control siguen rechazando cualquier ADS.
- Aplica la marca en el temporal antes de flush y promoción. Comprueba marca y contenido al confirmar y restaurar. Compara de nuevo los metadatos observados del origen; no equivale a una instantánea VSS.
- No muestra ni registra el contenido de la marca, que puede contener URL de origen y referencia. Las pruebas usan dominios inventados.

## Transporte remoto

`Test-KopiaZoneIdentifier.ps1` comprobó Kopia 0.23.1 fijado por SHA-256. Una conexión independiente de lectura, caché nueva y origen apartado conservó los bytes principales de cuatro archivos, pero perdió las tres marcas presentes, incluida la vacía. La medición corresponde a esta versión y plataforma, motivando el sobre remoto layout 2 con catálogo verificado.

Los envíos nuevos usan layout 2: `.foldertwin-remote-catalog.jsonl` contiene los bytes exactos del catálogo confirmado, y `data/` contiene el árbol. No hay un segundo catálogo autoritativo. Las etiquetas `ft-v2-job`, `ft-v2-generation`, `ft-catalog` y `ft-catalog-bytes` enlazan trabajo, fecha, hash y tamaño. El inventario remoto incluye un archivo adicional y dos directorios adicionales contando la raíz y `data/`. Los lectores anteriores buscan `ft-job`: no enumeran estos puntos como si pudieran restaurarlos completamente.

El lector descarga primero el catálogo, comprueba hash, esquema y totales y recupera todo o un archivo/carpeta a `.foldertwin-cloud-staging` dentro de una salida nueva. Rechaza rutas, tamaños o contenidos distintos. Aplica y verifica metadatos en staging antes de promover sin sobrescribir. Cancelar o fallar conserva staging y cualquier salida parcial para revisión. El ejecutor informa fases y recuentos. La interfaz permite elegir archivo o carpeta y conserva el estado del intento entre aperturas.

Los puntos remotos anteriores mantienen recuperación completa. Un envío preparado en layout 0 conserva ese formato al reintentarse; ese formato rechaza puntos que requieran una marca. La prueba comprueba además los bytes exactos de serialización de los registros anteriores, que forman parte de su cadena de hashes.

Con Kopia independiente se puede descargar el sobre completo identificado por las etiquetas nuevas. Conserva `data/` y el catálogo. El lector de FolderTwin verifica y vuelve a aplicar la marca antes de entregar los archivos. [Guía de emergencia](../RECUPERACION-NUBE.md)

## Aceptación de desarrollo

`--zone-local` aprobó tres puntos por modo, un único objeto CAS para veinte rutas, cambios exclusivos de marca con fecha conservada, reanudación interrumpida con origen apartado, cuarentena/deshacer/purga y seis mutaciones semánticas de catálogo. `--product-watch` observó una edición real de ADS con fecha principal conservada. La reconciliación integral sigue siendo necesaria ante avisos perdidos.

`--cloud-point` y `Test-CloudPoint.ps1` aprobaron seis puntos de layout 2 en archivos locales y S3 de loopback: origen y destino apartados, otra conexión, marcas distintas/vacías/ausentes/de 16 KiB, selección de archivo y carpeta, cancelación antes de promover y hash de catálogo incorrecto. También aprobaron un punto del formato remoto anterior. `--cloud-rclone` recuperó marcas opacas en dos fechas de cada perfil mediante almacenamiento local desechable. No son pruebas de cuentas reales.

El paquete portable verificado incluye estos recorridos dentro de su suite de aceptación sintética. Permanecen pendientes el comportamiento real de aplicaciones de escritorio al consumir la marca, los proveedores con cuentas reales, otra cuenta/equipo y la durabilidad física ([V1.md](../V1.md)).
