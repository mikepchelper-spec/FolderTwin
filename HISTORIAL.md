# Historial de versiones, retos superados y mejores prácticas

Este documento centraliza la evolución técnica de **FolderTwin**, los problemas de ingeniería resueltos durante su construcción y auditoría, las prácticas de diseño adoptadas en el código y las mediciones sintéticas de referencia. Sustituye a las antiguas bitácoras dispersas por commit.

> **Alcance actual:** Vista previa experimental para Windows 11 x64 sobre volúmenes locales NTFS, verificada con datos sintéticos y carpetas desechables. Las validaciones en hardware físico, piloto prolongado y cuentas reales en proveedores de nube permanecen abiertas en [V1.md](V1.md).

---

## 1. Evolución de versiones y etapas

| Etapa / Versión | Formato y arquitectura | Hitos principales y cambios clave |
| --- | --- | --- |
| **Alfa de laboratorio** | `FTCAT001`, `FTVERS01` y `FTVERS02` | Motor inicial sobre espacios temporales acotados (`DirectoryFence`), catálogos JSONL duplicados con confirmación binaria de 112 bytes, almacén de objetos por contenido (`objects/sha256/ab/<sha256>`), espejo protegido inicial, índice local SQLite reconstruible, horario básico y bandeja opcional. |
| **`v1.0.0-rc.1`** *(retirada)* | `FTVERS03` (monolito mixto en `experiments/`) | Primer asistente de cuatro pasos de producto, trabajadores separados con pausa/cancelación e historial durable, copia acumulativa, retención manual con cuarentena/purga e importación/exportación de configuración. **Retirada durante auditoría posterior** al detectar recreación de filas de historial que perdía foco de teclado y mezcla de informes antiguos dentro del ZIP congelado; se conservaron etiqueta y hashes históricos para trazabilidad. |
| **`v1.0.0-rc.2`** | `FTVERS03` | Corrección de estabilidad de foco y selección en WPF, separación entre identidad interna del paquete (`COMPILACION.json` + `SHA256SUMS.txt`) e informes posteriores de aceptación, y validación cruzada de lectura/restauración frente a respaldos creados con RC1. |
| **Serie post-RC2 (automatización y nube)** | `FTVERS03` + catálogo `v4` + sobre remoto `layout 2` | Vigilancia continua por trabajo (`FileSystemWatcher` + agrupación de ráfagas + reconciliación periódica), comparación acotada (`Scoped`), fin de vida verificable de metadatos tras purga (`version 3` del registro), conservación opaca de `Zone.Identifier` hasta 16 KiB y tercera copia cifrada 3-2-1 con **Kopia 0.23.1** y puente local **rclone 1.76.0** (`127.0.0.1`), incluyendo recuperación desde perfil vacío. |
| **Revisión integral de auditoría (`H-01` a `H-17`)** | `src/FolderTwin.Core` + `src/FolderTwin.App` (`FolderTwin.exe`) vs `tests/` | Resolución de 17 hallazgos de auditoría profunda (5 P0, 9 P1, 3 P2): restauración local limpia mediante staging oculto sin residuos `.foldertwin-*`; detección de borrado externo y *bitrot* en exclusivos de copia acumulativa; eliminación del interbloqueo cuarentena pasiva ↔ abandono de versiones; archivo histórico encadenado para que el registro vivo de 16 MiB nunca se agote; reparación de colas con ceros tras corte abrupto; comparación rápida `Scoped` extendida a `Versions`; catálogos de hasta 512 MiB parseados en un pase; staging de nube fuera de `C:`; consultas SQLite en solo lectura sin checkpoints WAL por lectura; hilo UI libre de E/S bloqueante; soporte real de Alto Contraste; y separación estricta entre binarios puros de producción y suites de prueba. |

---

## 2. Retos técnicos superados

### 2.1. Identidad física de volúmenes NTFS y confinamiento sin carreras TOCTOU
- **Reto:** En Windows, las letras de unidad (`D:`, `E:`) cambian al reconectar discos externos y una simple validación textual de rutas permite sustituciones mediante *junctions*, enlaces simbólicos o nombres reservados DOS (`CON`, `NUL`, `AUX`, `COM1`…`LPT9`) entre la comprobación y la apertura.
- **Solución:** `DirectoryFence` y `WindowsProbeNative` abren y retienen handles nativos de directorio, verifican `VOLUME_NAME_GUID` y los 24 bytes de identidad física (`VolumeSerialNumber` + `FileId` de 128 bits en `FILE_ID_INFO`), abren componentes intermedios relativos al handle padre rechazando *reparse points* inesperados y filtran dispositivos DOS en cada primitiva nativa antes de tocar disco.

### 2.2. Restauración local limpia sin exponer artefactos internos
- **Reto:** Escribir archivos temporales y recibos de recuperación directamente sobre la carpeta de destino elegida por el usuario podía dejar residuos `.foldertwin-*` visibles o impedir respaldar después esa carpeta restaurada.
- **Solución:** `VersionStore.Restore` prepara y verifica los contenidos dentro de un área oculta `.foldertwin-restore-staging` en la salida nueva, reutiliza handles de directorio padre agrupando rutas por carpeta, promueve cada archivo mediante `CopyRecovery.PublishData`, ejecuta `FlushDirectory` sobre las carpetas afectadas y retira el área temporal al completar, entregando únicamente el árbol limpio del usuario.

### 2.3. Integridad de archivos exclusivos en Copia acumulativa frente a ediciones o borrados externos
- **Reto:** En modo acumulativo, los archivos borrados del origen deben conservarse en `mirror/current`. Si alguien borraba o corrompía externamente en el destino uno de esos archivos exclusivos antes del siguiente respaldo, un análisis ingenuo podía omitirlo silenciosamente del nuevo catálogo.
- **Solución:** `AccumulativeConflicts` y `MirrorPreview` contrastan todos los archivos exclusivos presentes en `previous.Files` contra el estado real observado en `mirror/current` (existencia, longitud, hash SHA-256 y ausencia de colisiones archivo↔carpeta). Cualquier pérdida o alteración externa detiene la publicación con conflicto explícito para revisión.

### 2.4. Crecimiento indefinido del registro de generaciones y colas corruptas tras apagón
- **Reto:** El registro replicado (`.foldertwin-generations-v1.jsonl` y réplica) tiene un presupuesto vivo de 16 MiB para acotar memoria y tiempo de arranque. A lo largo de años de purgas y abandonos, conservar cada comprobante histórico dentro del archivo vivo terminaría llenando ese presupuesto. Además, un corte abrupto al extender una réplica puede dejar bytes nulos al final del archivo.
- **Solución:** `RegistryCheckpoint` compacta los comprobantes sellados de purga y abandonos antiguos hacia un archivo histórico encadenado (`.foldertwin-generations-archive-v1.jsonl` + réplica) enlazado por hash SHA-256, manteniendo acotado el registro vivo sin perder trazabilidad. En paralelo, `GenerationRegistry.Append` y los escritores de checkpoints reparan colas truncadas o con ceros cuando la otra réplica certifica íntegramente el prefijo esperado.

### 2.5. Coexistencia entre cuarentena reversible y abandono de planes incompletos
- **Reto:** Impedir el abandono de un plan de versionado incompleto mientras existiera una cuarentena ya finalizada (`quarantined`) esperando sus días de gracia creaba un bloqueo circular cuando un plan pendiente también impedía operar la limpieza.
- **Solución:** Se separó la comprobación de transiciones activas de retención (`RequireNoActiveRetentionTransition`) respecto del estado pasivo en espera de gracia, verificando explícitamente cualquier objeto rescatado entre cuarentena, deshacer y purga, y auditando el almacén CAS en una sola pasada con `AlternateLookup<ReadOnlySpan<char>>` sin relecturas SHA-256 redundantes.

### 2.6. Escalabilidad a 329 000 archivos en vigilancia cotidiana e índice SQLite
- **Reto:** Dos cuellos de botella aparecían en árboles grandes: (a) abrir y cerrar la base SQLite en modo lectura-escritura por cada carpeta navegada disparaba un checkpoint WAL al cierre (~314 ms por consulta, superando varios minutos al recorrer miles de carpetas), y (b) reescanear todo el árbol en cada aviso de `FileSystemWatcher` penalizaba el modo `Versions` y los catálogos con `Zone.Identifier` superaban 128 MiB.
- **Solución:**
  1. `VersionIndex` abre consultas de navegación con `SQLITE_OPEN_READONLY` + `PRAGMA query_only=ON`, ejecuta filtrado y paginación en SQL (`INSTR(LOWER(path), ?)` + `LIMIT/OFFSET`), poda vistas antiguas por LRU y ejecuta `PRAGMA wal_checkpoint(TRUNCATE)` una sola vez al cerrar la importación (recorrido completo de 329 000 rutas en **~3,5–4,6 s**).
  2. `BatchCatalog` procesa manifiestos de hasta **512 MiB** en un solo pase con `Utf8JsonReader`, evita parsear la réplica cuando coincide byte a byte con el primario válido y ofrece `ReadSummaryLocked` para consultas ligeras de bandeja.
  3. El modo `Versions` incorpora también comparación rápida acotada (`Scoped`) apoyada en las rutas notificadas por el monitor y metadatos (`Length`, `LastWriteUtc`), escalando dinámicamente el intervalo de reconciliación completa según el coste medido del árbol.

### 2.7. Conservación exacta de `Zone.Identifier` en NTFS y sobre Kopia
- **Reto:** Windows almacena la procedencia de descarga en el flujo alternativo `:Zone.Identifier`, pero abrirlo por nombre de ruta rompe el confinamiento por handle, `BackupSeek` requiere manejar archivos dispersos con decenas de rangos y Kopia 0.23.1 descarta los flujos alternativos NTFS al restaurar instantáneas S3.
- **Solución:** `ZoneIdentifierStream` lee y escribe exclusivamente sobre el handle ya verificado mediante `BackupRead`, `BackupSeek` y `BackupWrite` (soportando archivos dispersos con >31 bloques y exigiendo `FILE_WRITE_DATA` verificado antes de escribir). Para la nube, se diseñó el sobre remoto **`layout 2`** (`.foldertwin-remote-catalog.jsonl` verificado por hash + `data/`), que conserva y reaplica `Zone.Identifier` (incluida la distinción entre ausencia y flujo vacío) antes de promover la recuperación.

### 2.8. Tercera copia cifrada sin saturar `C:` ni acoplarse a conectores sin mantenimiento
- **Reto:** Preparar en `%LOCALAPPDATA%` el árbol temporal de subida a la nube podía llenar la unidad del sistema `C:` al respaldar carpetas grandes de discos secundarios; además, el backend directo `rclone` de Kopia está marcado sin mantenimiento.
- **Solución:** El área temporal se prepara en `.foldertwin-cloud-stage-<jobId>` dentro del mismo volumen del respaldo local (calculando SHA-256 en un único pase con `IncrementalHash` y clasificando `DestinationMissing` como reintentable si el disco local está desconectado), las cachés de Kopia se acotan a 64 MiB y los perfiles Filen/MEGA operan sobre `rclone serve s3` efímero en `127.0.0.1:0` (en solo lectura durante recuperaciones), manteniendo handles `FileShare.Read` abiertos sobre `kopia.exe` y `rclone.exe` frente a sustituciones en caliente.

### 2.9. Concurrencia entre procesos, fluidez del hilo WPF y ahorro en discos externos
- **Reto:** Sondear bloqueos abriendo archivos en modo truncamiento/creación o abrir cada 5 segundos la raíz de respaldo con `writable: true` para consultar horarios despertaba innecesariamente discos USB en reposo, y las llamadas síncronas a procesos/disco desde el hilo `Dispatcher` congelaban la ventana.
- **Solución:** Se sustituyeron los sondeos destructivos por bloqueos de rango de bytes centinela (`0x7FFF_FFFF_FFFF_FFFE`), se separaron locks compartidos de lectura (`FileShare.Read`) y exclusivos de escritura (`FileShare.None`), se movió toda E/S de disco/registro/procesos fuera del `Dispatcher`, se pausó `runtimeTimer` al minimizar a bandeja, se cambió la consulta periódica de raíces a `writable: false` y se corrigió `RouteTheme` para usar colores reales del sistema en Alto Contraste de Windows.

### 2.10. Aislamiento del binario de producción y distribución reproducible
- **Reto:** Mezclar las clases `*Probe` y `*Smoke` y decenas de banderas CLI de laboratorio dentro del ejecutable publicado exponía superficie interna innecesaria en el paquete de usuario.
- **Solución:** Se extrajo la arquitectura en `src/FolderTwin.Core` (`Library`) y `src/FolderTwin.App` (`FolderTwin.exe`, aceptando únicamente arranque normal, bandeja y comandos internos de trabajador, manteniendo compatibilidad de actualización desde instalaciones `CopyLab.exe` previas). Toda la maquinaria de ensayo vive en `tests/`, `--packaged-binary-audit` inspecciona las tablas ECMA-335 con `PEReader`/`MetadataReader` para certificar cero tipos `Probe`/`Smoke` filtrados, y dos compilaciones independientes con `Publish-Portable.ps1` + `New-PortableZip.ps1` producen exactamente el mismo hash SHA-256.

---

## 3. Mejores prácticas de ingeniería del proyecto

1. **El origen es sagrado (solo lectura estricta):** Ninguna operación de análisis, copia, verificación o restauración modifica, trunca ni cambia marcas de tiempo del árbol de origen.
2. **Planes inmutables antes de mutar estado:** Toda transición multicarpeta (espejo, acumulativo, cuarentena, purga de metadatos, abandono de versión, retirada de ejecutor instalado) escribe, vacía (`Flush(true)`), relee y enlaza por SHA-256 un plan en disco **antes** del primer movimiento o borrado.
3. **Publicación sin sobrescritura destructiva:** Los objetos CAS y catálogos se escriben en temporales exclusivos verificados por relectura y se promueven por handle sin pisar generaciones confirmadas; los archivos sustituidos en espejo/acumulativo pasan primero a un área `retired` recuperable.
4. **Índices y cachés prescindibles:** SQLite (`index.db`) y las cachés temporales sirven únicamente para acelerar navegación o transferencia; los catálogos JSONL duplicados + confirmaciones binarias en el destino bastan por sí solos para verificar y restaurar desde un perfil vacío (`Abrir respaldo existente`).
5. **Credenciales fuera de JSON y de registros:** Las claves S3/rclone y contraseñas de repositorio viajan por variables de entorno del proceso hijo o bajo DPAPI del usuario actual vinculadas a la identidad del trabajo; los diagnósticos exportables aplican lista blanca sin rutas ni nombres privados.
6. **Identidad de paquete separada de su aceptación:** El ZIP portable contiene `COMPILACION.json`, `COMPONENTES.json` (CycloneDX 1.6), `SHA256SUMS.txt` y `FolderTwin-source.tar`, pero nunca se reconstruye para inyectarse su propio informe de pruebas; la aceptación se ejecuta y verifica **después** sobre el ZIP extraído.
7. **Disciplina experimental honesta (`AGENTS.md`):** Toda prueba automatizada corre sobre datos sintéticos y carpetas desechables en `%TEMP%` o imágenes `.vhdx` aisladas en CI, declarando explícitamente qué validaciones de hardware físico o proveedores externos siguen pendientes.

---

## 4. Mediciones sintéticas de referencia

Los valores siguientes proceden de ejecuciones reales de los arneses del repositorio sobre Windows 11 x64 y `.NET 10` con datos sintéticos; sirven como línea base de ingeniería y no sustituyen pruebas en discos USB físicos ni enlaces WAN reales:

| Escenario medido | Arnés / Flujo | Resultado observado |
| --- | --- | --- |
| **Distribución CAS de 1 000 000 de objetos** (1 nivel `objects/sha256/ab/` vs 2 niveles en VHDX NTFS aislado) | `object-layout.yml` (`Run-ObjectLayout.ps1`) | **1 nivel elegido:** creación en `169,4 s`, 4 096 lecturas en `0,120 s` y enumeración en `0,371 s` (256 cubetas), frente a `396,0 s` y `3,064 s` en 2 niveles (65 536 carpetas). |
| **Versionado completo de 329 000 rutas** (4 096 objetos compartidos, manifiesto de ~75,3 MiB) | `CopyProbe --version-scale` | Inventario `1,12 s`, respaldo verificado `227,2 s`, listado `3,39 s`, verificación `3,90 s`, restauración de ruta `3,28 s` (pico residente `398 MiB`). |
| **Índice SQLite de 329 000 rutas** (importación + comparación + recorrido de 1 286 carpetas en solo lectura) | `CopyLab.Tests --index-scale` | Importación `~1,7–2,7 s`, reapertura de vista `0,003 s` y recorrido completo de las 1 286 carpetas en **`2,7–4,6 s`**. |
| **Retención a escala (3 generaciones × 329 000 rutas)** con cuarentena y purga | `retention-scale.yml` (`CopyProbe --retention-scale 329000`) | Revisión `3,23 s`, cuarentena `8,05 s` y purga `3,17 s` procesando catálogos secuencialmente (pico residente `~478 MiB`). |
| **Vigilancia continua y consumo en reposo** (edición automática + monitor en espera) | `CopyLab.Tests --product-monitor-smoke` | Confirmación tras edición desde señal del watcher en **`2,8–3,0 s`** (incluidos 2 s de ventana de calma); monitor en reposo con **`0,21 %` de un núcleo CPU** y `0 B` leídos de disco. |
| **Listado remoto de historial largo** (10 000 instantáneas sintéticas Kopia JSON) | `AutomationAcceptance --cloud-history` | Procesamiento completo de 10 000 fechas en **`0,55 s`** (`~47 MiB` asignados en memoria gestionada). |
