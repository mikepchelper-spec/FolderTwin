# Auditoría posterior a RC1

**Auditoría de software cerrada con RC2 experimental**. Las fuentes del ZIP son `d902f4c`. Se verificaron las descargas públicas y su manifiesto. RC1, del commit `e4e87b8`, conserva etiqueta, fuentes, hashes e informe histórico. Su ZIP oficial permanece retirado. [Aceptación de RC2](ENTREGA-RC2.md) · [Publicación y hashes](PUBLICACION.md)

## Alcance y reglas de evidencia

Tres modos sobre NTFS local, retención manual, interfaz y ejecutores existentes. Solo fixtures sintéticos identificados bajo el temporal, con reserva de espacio y límites de tiempo/tamaño. No se usaron respaldos personales, FreeFileSync, cortes eléctricos, reinicios ni cierre de sesión. Los logs y binarios de trabajo permanecen fuera del repositorio. Los resultados de proceso, caché o sistema de archivos no acreditan durabilidad del dispositivo ni recuperación universal.

Se distingue revisión estática, regresión automatizada, medición y observación de escritorio. Un caso que falla antes de alcanzar la frontera investigada no cuenta como reproducción. Un checksum recalculable valida estructura e integridad accidental; no autentica metadatos frente a otro escritor local.

## Hallazgos y seguimiento

| ID | Prioridad y alcance | Reproducción / causa | Corrección y evidencia actual |
|---|---|---|---|
| A01 | P1, control de trabajos | `StartRun` leía habilitación antes del lock de ejecución. Deshabilitar durante esa ventana no impedía el nuevo intento. | Lectura y gate dentro del lock; campaña verde de deshabilitación, retirada, habilitación fresca y exclusión inversa. Root revisó el orden y la exclusión sin un lock global nuevo. |
| A02 | P1, configuración importada | `ReviewImportedSource` reutilizaba ajustes leídos antes del lock y podía sobrescribir un cambio concurrente. | Lectura y gate dentro del lock, conservando global→ejecución→programación→ajustes. Control rojo aislado con el orden anterior reproduce pérdida de retirada/nombre/hora y revisión de trabajo ya habilitado; control verde y revisión de Root completados. |
| A03 | P0 según MANTENIMIENTO, frontera del caché local | La validación de archivos SQLite terminaba antes de abrirlos mediante el VFS. Tres sustituciones sintéticas DB/WAL/SHM alteraron una base centinela propia situada fuera del caché. Los controles con enlace ya presente rechazaron la operación. | Se fijan DB/WAL/SHM/journal mediante handles validados hasta cerrar SQLite. Seis casos verdes, más uso normal/reapertura/reconstrucción/liberación y rechazo de bases ajenas frías o con WAL pendiente sin cambiar nombres ni bytes. Segunda revisión independiente completada. Es un escritor local concurrente con acceso al caché, no una entrada remota demostrada. |
| A04 | P2, información de estado | Verificar un punto antiguo ocultaba en bandeja el fallo del último respaldo. | Se siguen por separado respaldo y verificación. Regresión verde: origen ausente, verificación sin origen, fecha/identidad conservadas y nuevo respaldo que resuelve su incidencia. |
| A05 | P2, disponibilidad | Acumulativo trataba `case.txt` y `CASE.txt` como dos entradas, rechazando un renombrado solo de mayúsculas. | Conserva casing del destino y padres; compara conflictos bajo la misma equivalencia. Oráculo ampliado verde: nueve casos y 52 puntos restaurados offline, incluidos exclusivo bajo padre renombrado, interrupción Planned/Resume, revisión obsoleta con unión idéntica y cambios archivo↔carpeta rechazados sin cambios de datos. Segunda revisión independiente completada. |
| A06 | P2, lectura de metadatos | Elementos `null` en colecciones de retención y abandono escapaban de la validación del contrato. | Guards explícitos antes de desreferenciar. Nuevos casos semánticos conservan snapshots de bytes/rutas ante rechazo. |
| A07 | P2, disponibilidad | Secuencia `long.MaxValue` en historial de producto o legado lanzaba `OverflowException`. | Límite explícito con rechazo controlado, sin reutilizar secuencias. El caso legado se reprodujo con un sobre válido; campaña de 24 lectores semánticos verde y segunda revisión completadas. |
| A08 | P2, programación | La inspección XML aceptaba restricciones de batería o disparador deshabilitado como política equivalente. | Siete variaciones rechazadas. La aceptación nativa del paquete pasó los tres modos, el reintento tras 300,9 s, la retirada durante la espera y la conservación de la incidencia. |

Prioridades se refieren al comportamiento demostrado. No se ha demostrado pérdida de contenido de respaldo, escritura en origen ni evasión de una aprobación destructiva. A03 sí demostró una escritura fuera del caché autorizado, sobre un centinela exclusivamente sintético. La retirada preventiva de RC1 no borra su evidencia ni afirma que el daño se haya producido en otros equipos.

## Matriz de invariantes y fronteras

| Invariante / riesgo | Código y llamadores | Evidencia existente y nueva | Hueco o límite |
|---|---|---|---|
| Origen abierto solo para lectura; no copiar una instantánea incoherente como completa | `VerifiedCopy`, `FolderBatch`, `VersionStore`; runners de los tres modos | Archivos ocupados, cambio concurrente, inventario parcial; oráculo compara bytes y metadatos del origen antes/después | Sin VSS ni coherencia entre múltiples archivos abiertos |
| Raíces locales, identidad de volumen/directorio y confinamiento | `DirectoryFence`, `WindowsProbeNative`, `LocalJobRoots`, `ProductJobRegistry` | Paths/product-roots/product-jobs: sustitución, enlaces, solapamiento, GUID de volumen, revalidación de identidad | Los handles fijan componentes; no son una defensa ante un administrador que modifica el dispositivo |
| Archivos ordinarios sin reparse/hardlinks/nube/EFS | `OpenPlainFile`, `OpenPlainDirectory`, `RequirePlain` | Paths, inventario y nueva frontera SQLite A03. VHDX 42210313861 pasó con el motor corregido | USB físico y nube real pendientes |
| Publicar solo contenido copiado y verificado | `VerifiedCopy`, `CopyRecovery`, `VersionStore.CopyAndConfirm` | Fallos por fases, recibos, flush, hash, recuperación del temporal con origen ausente | Flush/relectura no prueban persistencia física del hardware |
| Intento durable y reanudable, sin éxito ficticio | `ProductRun`, `ProductRunner`, pending intents | Pausa/cancelación/terminación de trabajador propio y reapertura sin origen; A01/A07 | Nuevas intercalaciones no sustituyen ciclos reales de Windows |
| Manifiestos duplicados, cierre y referencia esperada | `BatchCatalog`, `GenerationRegistry` | Catalog/registry probes, split de réplicas, hash-chain, lector independiente | Recuperar con una réplica válida es degradado; GC exige ambas |
| No eliminar ante inventario incompleto o conflicto | `MirrorPreview`, `MirrorExecution`, `Accumulative` | Mirror/accumulative probes: vacío, ausente, parcial, masivo, edición externa, ready obsoleto | A05 corregido y con regresión; las aprobaciones se ligan al inventario revisado |
| Contenido e históricos coinciden con un modelo independiente | `DifferentialAuditProbe` contra API de los tres modos | Seeds 1337/7331, mutaciones mezcladas, Unicode, vacío, igual fecha/tamaño, exclusivo, restauración sin origen; nueve casos verdes tras corregir A05 | Composición de casing de carpeta, conflictos y tipos añadida; no se repite escala 329k |
| Limpieza solo con referencias íntegras y sin operaciones pendientes | `Retention`, `RetentionCleanup`, `RetentionJournal` | RetentionProbe por fases/splits/proceso terminado/rescate/locks; RecoveryAudit: 40 selecciones calendario independientes, compartidos y corrupción | Retención manual; gracia verificada por reloj no equivale a reloj confiable |
| Cuarentena reversible antes de purga | `Begin`, `Undo`, `Purge`, `Continue`, `RescueQuarantinedObject` | Seis puntos pequeños, origen offline, referencias conocidas por hash independiente; bytes/rutas/fecha restaurados | Un punto expirado puede seguir recuperándose si sus objetos siguen activos por otro punto; no se promete recuperación tras purga |
| Recuperación nueva, sin índice ni origen | `IndependentRestore`, `ProductRestore` | Oracle offline, lector independiente, corrupción que falla antes de salida, exclusión con registro de trabajos | No sobrescribir una carpeta de recuperación existente |
| Exclusión de operaciones sobre el mismo trabajo/almacén | `ProductRun`, settings, scheduler, maintenance, store locks | A01/A02 deterministas; restoration worker vs backup/GC/registro; locks fallan sin esperar | Inversiones de orden no demostraron deadlock porque adquisición no espera; no añadir un lock global persistente |
| Índice reconstruible, sin autoridad sobre el respaldo | `VersionIndex`, `LocalSqlite`, browser | Corrupción/reconstrucción y navegación existentes; seis casos nuevos de sustitución del VFS | A03 corregido y con regresión; no confiar en NOFOLLOW como protección frente a hardlinks |
| Estado e incidencias independientes por operación/trabajo | `TraySnapshot`, `ProductWindow`, histories | A04 regresión, product surface smoke y revisión de callers | Lector de pantalla, DPI y escritorio manual completo siguen externos |
| Política de horario fiel a lo mostrado | `WindowsSchedule` | A08 con siete mutaciones XML y control de defaults. Prueba nativa desde RC2 | Ciclos de Windows siguen externos |
| Instalación solo adopta archivos inventariados y hashes conocidos | `LabInstallation`, manifest reader | Cinco interrupciones, actualización/compatibilidad/reanudación | Nunca adoptar automáticamente archivos ajenos |
| Retirada conserva datos ajenos y diario reanudable | `LabInstallation.Removal` | Siete checkpoints; nuevos casos de archivo ajeno insertado después de inventariar en raíz y docs | Puede haber eliminado archivos propios antes de detenerse: no es retirada atómica |
| Formatos anteriores siguen legibles | Readers de catálogo, journal, intents y compatibilidad | WPF empaquetado abrió FTVERS01/02. Motor RC2 verificó y restauró tres trabajos FTVERS03 creados con RC1 | Downgrade, otra cuenta y otro equipo siguen fuera de la evidencia |
| Distribución identifica exactamente fuentes/runtime/licencias | Publish/Test-Portable, CI | Dos builds limpias, verificación de ZIP exacto, compatibilidad y cuatro descargas públicas comprobadas | El informe posterior queda como adjunto separado |

## Recorrido, coordinación y reanudación

El asistente `ProductWindow` usa `LocalJobRoots.ReviewDedicatedDestination` y `ProductJobRegistry.Create` para ligar identidades y destino. El ejecutor `ProductRunner.Run` reabre el registro, toma su lease y abre `VersionStore.OpenProduct`. Versionado toma el inventario y publica por `CreatePoint` o continúa su intención. Espejo usa `PreviewMirror` → `BeginMirror`/continuación. Acumulativo pasa por ese diario con su inventario bruto y una unión del origen y el destino, conservando exclusivos. Los tres pasan por los objetos verificados y catálogos duplicados. Para recuperar, el trabajador vuelve a revisar la salida y abre el punto con el lector de almacén. Retención toma el lock del almacén y contrasta el registro esperado, ambos catálogos y referencias antes de mover o purgar.

| Operación | Orden y frontera comprobada | Evidencia |
|---|---|---|
| Registro, importación y revisión de salida | Registro global. Restauración anuncia su intención activa antes de repetir la comprobación de solapamiento bajo ese lock | ProductJobRegistryProbe, ProductConfigurationProbe y ProductRestoreProbe con trabajador retenido |
| Inicio manual, verificación y controles | Lock de ejecución antes de leer ajustes. Los controles se ligan a la intención y secuencia del intento activo | A01 y ProductRunProbe, incluidas órdenes antiguas y terminación de proceso |
| Horario y reintentos | El ejecutor programado retiene su lease de programación y vuelve a pasar por el registro y la ejecución. Una colisión se rechaza sin esperar el otro lock | ProductScheduleProbe y revisión de `WindowsSchedule` |
| Edición y revisión de origen importado | Ejecución → ajustes. La revisión importada toma primero registro global y luego ejecución → programación → ajustes | A01/A02 con callbacks en el límite, sin esperas temporizadas |
| Aplicación y retirada | Registro global → leases ejecución/programación por ID → segunda instantánea de ajustes. El runtime verifica la instalación antes de retirar archivos | ProductWindow.ApplicationSmoke e InstallationProbe |
| Almacén, restauración y limpieza | Lock del almacén para catálogo, verificación, copia, restauración y retención. Revisión y aplicación comprueban estado y huellas | ProductRestoreProbe, MirrorProbe, ProductStoreProbe y RetentionProbe |

El orden no es globalmente idéntico entre horarios, registro y ejecución. Estas adquisiciones usan `FileShare.None` y fallan de inmediato ante conflicto, con liberación de los handles ya adquiridos. La revisión no encontró espera circular en esos caminos. Los recorridos sintéticos cubren intercalaciones concretas y no sustituyen un ensayo prolongado entre sesiones de Windows.

### Límites de interrupción

| Límite durable | Prueba que exige preservación o reanudación |
|---|---|
| Escritura, flush, recibo, verificación y promoción de objeto | Probe principal y ResumeProbe. Trabajador propio terminado y reabierto con el mismo plan |
| Intención y preparación de directorios/registro/almacén | ProductStoreProbe recorre ProductStoreStep y PendingStep. ResumeProbe comprueba intención parcial |
| Primario, réplica y recibo | CatalogProbe y VersionProbe recorren CatalogStep. RegistryProbe cubre ambas réplicas y reparación del prefijo común |
| Plan, preparación, retiro, promoción y confirmación de actual | MirrorProbe y AccumulativeProbe recorren sus puntos de corte. El oráculo añade reanudación tras un renombrado de carpeta que conserva exclusivos |
| Abandono de plan y registro de expiración | VersionAbandonProbe recorre AbandonStep y cortes de catálogo/registro. WPF comprueba que se conserva el plan incompatible |
| Cuarentena, deshacer y purga | RetentionProbe cubre cuatro, cuatro y tres pasos respectivamente, además de un trabajador terminado. RecoveryAudit comprueba el contenido desde catálogos offline |
| Instalación y retirada | InstallationProbe cubre archivos parciales y verificados, registro/confirmación, siete pasos de retirada y dos llegadas tardías |

## Contratos de lectura

| Lector | Límites y validación | Campaña |
|---|---|---|
| `BatchCatalog.Parse` | 8/128 MiB, línea 64 KiB, 10k/350k entradas, UTF-8 estricto, orden, nombres, footer y hash | CatalogProbe: schema, traversal, duplicados, encoding, truncamiento y límites |
| `PendingBatch.ReadEnvelope` | 64 KiB, profundidad 16, checksum, IDs y payload estricto | ResumeProbe y publicación corrupta |
| `ProductJobRegistry.Decode` / configuración | 64 KiB, SHA, profundidad 12, null, identidades; configuración hasta 32 aliases | ReaderAudit: siete variaciones semánticas válidamente rechecksummed |
| `GenerationRegistry` | Dos réplicas de hasta 16 MiB, línea 2 KiB, profundidad 8, secuencia/hash-chain/eventos | RegistryProbe: escrituras parciales y split |
| `RetentionJournal` | 128 MiB, profundidad 16, política, conjuntos disjuntos y digests | Diez casos nuevos, incluidos elementos null |
| `MirrorJournal` | 8224 bytes, profundidad 8, versiones 1/2/3, IDs/hash/origen/anterior | MirrorProbe por fases y cambios de revisión |
| `VersionAbandon` | 16 KiB + hash, profundidad 12, marcador y hasta cinco artefactos | Cinco casos nuevos, incluido elemento null |
| `VersionJob` / producto | 64 KiB, sobre estricto y checksum; intent/result ligado al trabajo | Nuevos límites de secuencia máximos, sin reutilización |
| `AttemptHistory` | 256 KiB, línea 4 KiB, hasta 12 incidencias, null explícito | Probes de historial y producto |
| Manifest de instalación / diario de retirada | 1 MiB/4096 rutas y 2 MiB/profundidad 8; UTF-8, SHA, identidad, nombres, duplicados | InstallationProbe, incorporación tardía de archivo ajeno |

La nulabilidad de C# no valida por sí sola elementos de colecciones deserializadas: [límites oficiales de System.Text.Json](https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/nullable-annotations). Los defaults del Programador se contrastan con el [esquema oficial](https://learn.microsoft.com/en-us/windows/win32/taskschd/task-scheduler-schema). Las comparaciones de casing se evalúan sobre el alcance NTFS normal de Windows; no se promete soporte para directorios configurados como sensibles a mayúsculas ni conservación exacta del spelling restaurado por el oráculo case-insensitive.

## Decisión sobre SQLite y segunda revisión

El handle principal valida la cabecera SQLite y el `application_id` big-endian antes de abrir el VFS o crear sidecars. Los cuatro handles permiten compartir lectura/escritura, pero no renombrar ni borrar los nombres comprobados, y se liberan después de cerrar SQLite. `PERSIST_WAL` evita retirar WAL/SHM al cerrar; no se presenta como desactivación del checkpoint. Solo una caché vacía y sin diarios con contenido se inicializa mediante journal MEMORY; cambia después a WAL/FULL antes de las operaciones normales. La primera propuesta falló con `SQLITE_IOERR_DELETE` y fue descartada: no se consideró una regresión aprobada. No se escriben cabeceras SQLite manualmente ni se añade un VFS propio.

Fuentes: [CreateFileW y compartición](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-createfilew), [cabecera SQLite](https://sqlite.org/fileformat.html#the_database_header), [PERSIST_WAL](https://sqlite.org/c3ref/c_fcntl_begin_atomic_write.html), [modos de journal](https://sqlite.org/pragma.html#pragma_journal_mode). Se observó `winsqlite3.dll` 3.53.4 en Windows build 26100; otros equipos dependen de su SQLite del sistema. No se adopta EXCLUSIVE permanente.

SQLite puede crear temporales de ordenación bajo el VFS y el temporal del sistema con nombres exclusivos; esos temporales no son los cuatro nombres fijados. No se cambió `temp_store` ni se promete que toda E/S de SQLite quede en la carpeta de caché. [Comportamiento documentado de temporales](https://sqlite.org/tempfiles.html). La corrección no crea un sandbox contra un proceso que controle completamente la misma cuenta.

Un fallo durante la inicialización MEMORY o un rollback journal antiguo puede dejar la caché inutilizable; se conserva y la interfaz usa los catálogos mediante el fallback. No se afirma haber validado cada estado de crash interno de SQLite. Los respaldos no dependen de esa base. Las pruebas de reapertura/reconstrucción y el recorrido WPF validan el camino normal y el fallback ante caché perdida/dañada; no prueban durabilidad física.

La primera aceptación empaquetada, [42210266286](https://github.com/mikepchelper-spec/FolderTwin/actions/runs/42210266286), pasó los arneses del motor y reprodujo el manifiesto en dos árboles, pero se detuvo al borrar solo `index.db` en el smoke de versiones. Con PERSIST_WAL ese escenario deja diarios, así que la apertura ahora distingue una caché ausente de una caché incompleta. Conserva todos los archivos de una caché incompleta sin crear una base vacía y permite seguir desde los catálogos. La prueba exige ese resultado y después una reconstrucción al apartar toda la caché sintética. SQLite documenta que el WAL puede contener transacciones confirmadas que aún pertenecen a la base original. [Persistencia del WAL](https://www.sqlite.org/wal.html#the_wal_file)

Un segundo agente revisó el orden y duración de los handles, la inicialización, las bases ajenas y el fallback. También revisó acumulativo, bandeja, límites de secuencia y guards de lectores. Otro agente revisó la corrección de diarios huérfanos. La revisión de llamadores y errores se contrastó con las regresiones integradas y la aceptación del paquete.

## Recursos y entrega

La nueva medición de polling usa 100 y 1000 intentos sintéticos, cinco muestras calientes por tamaño, reserva de 2 GiB, payload máximo de 32 MiB y presupuesto cooperativo de 120 s. Se ejecutó sin builds ni campañas de E/S concurrentes del equipo de agentes.

| Intentos | Mediana ms | CPU ms | Lecturas proceso | Bytes leídos | Asignados | Delta máximo handles/muestra | Working set final |
|---:|---:|---:|---:|---:|---:|---:|---:|
| 100 | 51,9118 | 62,5 | 407 | 58.847 | 2.166.504 | +1 | 70.180.864 |
| 1000 | 359,2248 | 375 | 4007 | 573.648 | 17.591.704 | +1 | 90.992.640 |

Cero bytes escritos durante cada consulta; payload de fixture de 539.355 bytes al final. Son contadores del proceso y caché caliente, no E/S física ni latencia completa de la interfaz. El crecimiento es lineal y no demuestra ausencia de fugas a largo plazo. La inspección del caller confirma que la ventana consulta mediante `Task.Run`, evita solapamientos de polling y no reconstruye filas sin cambios; la bandeja consulta de forma asíncrona cada cinco minutos. Decisión: conservar el historial completo y las incidencias antiguas; no introducir truncado, caché de autoridad, nuevos ajustes de prioridad ni una optimización especulativa con estos dos tamaños. El coste de muchos trabajos con historiales grandes sigue siendo un límite de esta medición.

El recorrido WPF integrado local pasó con el estado corregido: instalación/actualización/retirada (incluidas las dos incorporaciones tardías), tres modos, búsqueda/recuperación y caché dañada, foco/temas/tamaños, trabajadores y controles, retención, configuración y gestión de aplicación. Las consultas de bandeja y política también pasaron sus regresiones específicas. Es automatización de la aplicación, no una observación manual ni una certificación del lector.

El oráculo de 52 puntos tardó 15,761 s localmente y alcanzó 58.916.864 bytes de máximo residente. La campaña de compatibilidad se ejecutó con el motor extraído del ZIP final. Duró 18,980 s incluidas las dos compilaciones del arnés. El informe registra sus hashes y el resultado independiente de bytes y árbol. [Aceptación de RC2](ENTREGA-RC2.md)

Windows CI 42212336681 aprobó la candidata `d902f4c` en 11 min 16 s. La campaña empaquetada de historial registró 13,52 ms y 129,08 ms de mediana para 100 y 1000 intentos, con cinco muestras por tamaño. Las diferencias de entorno impiden compararlas como una aceleración del código. La escala de 329.000 rutas conserva la identidad de su motor anterior. [Aceptación Windows](https://github.com/mikepchelper-spec/FolderTwin/actions/runs/42212336681)

Validaciones externas pendientes: observación de 7–14 días, USB NTFS físico, reinicio/suspensión/sesión, otra cuenta/equipo, nube real y accesibilidad que requiere lector, escalado y otro entorno. No bloquean esta auditoría de software y no se consideran aprobadas.
