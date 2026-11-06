# Auditoría posterior a RC1

Estado: **en curso**. Este informe no acepta todavía una nueva distribución. RC1 (fuentes `e4e87b8`, etiqueta `v1.0.0-rc.1`) se retiró: el ZIP oficial ya no está disponible; se conservan la etiqueta, las fuentes, los hashes y el informe histórico. No se retargetea esa etiqueta. Las descargas previas y las cachés externas no se pueden revocar.

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
| A08 | P2, programación | La inspección XML aceptaba restricciones de batería o disparador deshabilitado como política equivalente. | Siete variaciones rechazadas: disparador, batería explícita/implícita, demanda, solo inactividad y solo red. Verde XML-only, sin crear tareas; aceptación nativa pendiente. |

Prioridades se refieren al comportamiento demostrado. No se ha demostrado pérdida de contenido de respaldo, escritura en origen ni evasión de una aprobación destructiva. A03 sí demostró una escritura fuera del caché autorizado, sobre un centinela exclusivamente sintético. La retirada preventiva de RC1 no borra su evidencia ni afirma que el daño se haya producido en otros equipos.

## Matriz de invariantes y fronteras

| Invariante / riesgo | Código y llamadores | Evidencia existente y nueva | Hueco o límite |
|---|---|---|---|
| Origen abierto solo para lectura; no copiar una instantánea incoherente como completa | `VerifiedCopy`, `FolderBatch`, `VersionStore`; runners de los tres modos | Archivos ocupados, cambio concurrente, inventario parcial; oráculo compara bytes y metadatos del origen antes/después | Sin VSS ni coherencia entre múltiples archivos abiertos |
| Raíces locales, identidad de volumen/directorio y confinamiento | `DirectoryFence`, `WindowsProbeNative`, `LocalJobRoots`, `ProductJobRegistry` | Paths/product-roots/product-jobs: sustitución, enlaces, solapamiento, GUID de volumen, revalidación de identidad | Los handles fijan componentes; no son una defensa ante un administrador que modifica el dispositivo |
| Archivos ordinarios sin reparse/hardlinks/nube/EFS | `OpenPlainFile`, `OpenPlainDirectory`, `RequirePlain` | Paths, inventario, laboratorio VHDX previo; nueva frontera SQLite A03 | Revalidar VHDX afectado tras correcciones del motor |
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
| Política de horario fiel a lo mostrado | `WindowsSchedule` | A08 XML-only con cinco mutaciones y control de defaults | Requiere aceptación nativa desde el paquete nuevo |
| Instalación solo adopta archivos inventariados y hashes conocidos | `LabInstallation`, manifest reader | Cinco interrupciones, actualización/compatibilidad/reanudación | Nunca adoptar automáticamente archivos ajenos |
| Retirada conserva datos ajenos y diario reanudable | `LabInstallation.Removal` | Siete checkpoints; nuevos casos de archivo ajeno insertado después de inventariar en raíz y docs | Puede haber eliminado archivos propios antes de detenerse: no es retirada atómica |
| Formatos anteriores siguen legibles | Readers de catálogo, journal, intents y compatibilidad | Probes existentes y secuencias límite nuevas, sin migrar formatos | Aceptación del paquete y guía de reapertura pendientes |
| Distribución identifica exactamente fuentes/runtime/licencias | Publish/Test-Portable, CI | Dos builds limpias y aceptación del RC1 son históricas | Nueva RC obligatoria por cambios binarios; no incluir su propio informe posterior dentro del ZIP |

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

Un segundo agente revisó de forma independiente el orden y duración de locks/pins, bootstrap, bases ajenas y fallback; también revisó los cambios de acumulativo, bandeja, límites de secuencia y guards de lectores sin hallar otro defecto por lectura. El autor de una corrección no fue su único revisor. Root revisó código y reproduce las regresiones integradas; la aceptación del paquete sigue siendo un hito distinto.

## Recursos y resultados pendientes

La nueva medición de polling usa 100 y 1000 intentos sintéticos, cinco muestras calientes por tamaño, reserva de 2 GiB, payload máximo de 32 MiB y presupuesto cooperativo de 120 s. Se ejecutó sin builds ni campañas de E/S concurrentes del equipo de agentes.

| Intentos | Mediana ms | CPU ms | Lecturas proceso | Bytes leídos | Asignados | Delta máximo handles/muestra | Working set final |
|---:|---:|---:|---:|---:|---:|---:|---:|
| 100 | 51,9118 | 62,5 | 407 | 58.847 | 2.166.504 | +1 | 70.180.864 |
| 1000 | 359,2248 | 375 | 4007 | 573.648 | 17.591.704 | +1 | 90.992.640 |

Cero bytes escritos durante cada consulta; payload de fixture de 539.355 bytes al final. Son contadores del proceso y caché caliente, no E/S física ni latencia completa de la interfaz. El crecimiento es lineal y no demuestra ausencia de fugas a largo plazo. La inspección del caller confirma que la ventana consulta mediante `Task.Run`, evita solapamientos de polling y no reconstruye filas sin cambios; la bandeja consulta de forma asíncrona cada cinco minutos. Decisión: conservar el historial completo y las incidencias antiguas; no introducir truncado, caché de autoridad, nuevos ajustes de prioridad ni una optimización especulativa con estos dos tamaños. El coste de muchos trabajos con historiales grandes sigue siendo un límite de esta medición.

El recorrido WPF integrado local pasó con el estado corregido: instalación/actualización/retirada (incluidas las dos incorporaciones tardías), tres modos, búsqueda/recuperación y caché dañada, foco/temas/tamaños, trabajadores y controles, retención, configuración y gestión de aplicación. Las consultas de bandeja y política también pasaron sus regresiones específicas. Es automatización de la aplicación, no una observación manual ni una certificación del lector.

El oráculo de 52 puntos tardó 15,761 s y alcanzó 58.916.864 bytes de máximo residente del proceso. La campaña de compatibilidad real RC1→motor corregido tardó 7,4465 s, con dos builds del mismo harness y tres respaldos creados por el binario anterior. Sus hashes y el cotejo con el binario final se registrarán en la aceptación posterior. No valida downgrade ni otra cuenta.

Falta aceptar un paquete nuevo reproducible y comprobar su publicación y descargas. CI añade las campañas acotadas a la aceptación y mantiene un máximo de 25 minutos para el conjunto ampliado (antes 18), incluidas dos builds y horarios nativos. No se repite la escala de 329.000 rutas; sus resultados anteriores conservan la identidad del motor medido. El procedimiento de piloto permanece en `PILOTO.md`.

Validaciones externas pendientes: observación de 7–14 días, USB NTFS físico, reinicio/suspensión/sesión, otra cuenta/equipo, nube real y accesibilidad que requiere lector, escalado y otro entorno. No bloquean esta auditoría de software y no se consideran aprobadas.
