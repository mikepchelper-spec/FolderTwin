# Aceptación del piloto controlado

Estado revisado desde `718391c`. Esta matriz distingue pruebas de software con datos sintéticos de comprobaciones externas. «Verificado» significa que el arnés indicado cubre el caso y pasó en la [validación Windows 41950591185](https://github.com/mikepchelper-spec/FolderTwin/actions/runs/41950591185); no acredita durabilidad física ni habilita datos personales. Los cambios siguientes deben actualizar sus filas y evidencia.

## Etapa 2 — Copia y recuperación

| Requisito | Implementación / evidencia inspeccionada | Estado y pendiente |
|---|---|---|
| Elegir carpetas y revisar recuento, bytes y capacidad | `LabWorkspace`, `MainWindow`; `--workspace`, WPF `--smoke` | Verificado en espacios registrados y dedicados. |
| Origen intacto, destinos solapados o sustituidos rechazados | `DirectoryFence`, `DestinationBinding`; `--workspace`, `--paths` | Verificado por software; cambio de letra también en laboratorio de volumen desechable documentado en PLAN. |
| Archivos nuevos, Unicode, vacíos, carpetas vacías y rutas largas | `FolderBatch`, `VerifiedCopy`; prueba base, `--batch`, `--paths`, WPF | Verificado dentro de la matriz de metadatos soportados. |
| Copia parcial, incidencia por ruta y reintento | `PendingBatch`, historial; `--batch`, `--workspace`, WPF | Verificado. La copia plana actual confirma un lote inicial; no implementa todavía actualizaciones acumulativas de un lote confirmado. Auditar este hueco junto al motor de espejo, sin presentar el lote inicial como ese modo completo. |
| Escritor abierto y diagnóstico orientativo | Lectura compartida sin escritores, `FileUseDiagnostics`; prueba base y WPF | Verificado con escritor sintético y fallback; no se cierran aplicaciones. |
| Inventario incompleto, límites, offline, enlaces y datos no soportados | `FolderInventory`; `--inventory`, `--batch`, `--workspace` | Verificado mediante atributos y fallos sintéticos. Nube real y ACL de otra cuenta requieren entorno externo. |
| Cancelar, pausar entre archivos, progreso y cerrar/reabrir | `MainWindow`, `PendingBatch`; `--resume`, WPF | Verificado; la confirmación ya iniciada puede terminar. |
| Restaurar sin origen y comparar contenido/metadatos | `BatchCatalog`; `--catalog`, `--workspace`, WPF | Verificado para fechas/atributos declarados; permisos heredados. |

## Etapa 3 — Versionado

| Requisito | Implementación / evidencia inspeccionada | Estado y pendiente |
|---|---|---|
| Objetos compartidos inmutables y versiones por fecha | `VersionStore`, SHA-256; `--versions` | Verificado; no usa enlaces duros. |
| Cambio con igual tamaño/fecha, renombrado y eliminación | Revisión completa; `VersionProbe.CheckFullReview`, WPF | Verificado; el renombrado puede aparecer como baja/alta. |
| Publicación y reanudación por transición | `PendingBatch`, `BatchCatalog`; `--versions`, `--resume` | Verificado por inyección y terminación de proceso; no es un corte eléctrico. |
| Catálogos duplicados, truncado, conflictos y hashes inválidos | `BatchCatalog`; `--catalog`, `--versions` | Verificado; una réplica válida permite recuperación con aviso. |
| Recuperación independiente sin SQLite ni origen | `--recover-version`; `VersionProbe` lanza otro proceso | Verificado incluso con una réplica dañada. |
| Índice reconstruible, carpetas, cambios y archivo eliminado | `VersionIndex`, `VersionBrowser`, WPF | Verificado con índice perdido/dañado; navegación con 329.000 filas en workflow de distribución. |
| Comparación cuyo punto anterior expiró | `VersionWindow` y retención | Corregido y verificado localmente en WPF: muestra expiración y bloquea recuperar el eliminado hasta deshacer la cuarentena. Validación Windows 41951815462 aprobada. |
| Escala y distribución de objetos | `--version-scale`, workflow `object-layout.yml`; PLAN | 329.000 rutas y comparación completa de un millón de objetos documentadas. Distribución de un nivel decidida. |

## Etapa 4 — Automatización

| Requisito | Implementación / evidencia inspeccionada | Estado y pendiente |
|---|---|---|
| Ejecutor independiente y cierre de UI | `VersionJob`, `LabWorker`; `--jobs`, WPF | Verificado con trabajador real y recuperación tras terminarlo. |
| Identidades, secuencia y conservación de fallos por trabajo | `VersionJob`; `JobProbe`, `TrayProbe` | Verificado; el reloj atrasado no reordena el historial. |
| Horario con instalación estable y definición comprobada | `LabInstallation`, `WindowsSchedule`; `--schedule-smoke` desde paquete | Verificado con disparador real próximo, actualización diaria y retirada. |
| No solapar y no apropiarse de tareas ajenas | Bloqueos de trabajo/almacén, política IgnoreNew, comprobación de propietario/acción | Pruebas de bloqueo en `--jobs` y definición en ScheduleProbe; ampliar cruce de disparadores manuales/programados si falta evidencia directa. |
| Destino ausente/reconectado y reintentos finitos | `VersionJob`, Programador, laboratorio de volumen | `--jobs` verifica ausencia/sustitución, vuelta de las raíces registradas, nuevo punto íntegro y conservación de incidentes; también bloquea el trabajador durante una operación manual. Validación Windows 41951815462 aprobada. Los reintentos del Programador son configuración comprobada, no un ensayo completo de cinco minutos. |
| Bandeja opcional, edad, avisos deduplicados y ejecutar ahora | `LabTray`, `TraySnapshot`, `TrayProbe` | Verificado con varios trabajos y punto expirado; entrega visible de notificaciones depende del entorno. |
| Inicio de sesión y horario perdido tras suspensión/reinicio | Tarea de usuario interactivo, StartWhenAvailable para hora | Definición verificada. Ciclo real en VM/equipo pendiente; sin sesión iniciada esta modalidad no ejecuta. |
| Impacto antes de ajustar prioridad | Mediciones de copia, índice y bandeja en PLAN | No se aplicaron ajustes propios. Carga interactiva HDD/SSD y CPUs distintas pendiente externa. |

## Etapa 5 — Retención y espejo

| Requisito | Implementación / evidencia inspeccionada | Estado y pendiente |
|---|---|---|
| Expectativas de generaciones y expiraciones durables | `GenerationRegistry`, FTVERS02; `--registry` | Verificado: secuencia, hashes, identidad y réplicas iguales; reparación solo desde evento esperado. |
| Política revisable y referencias por origen | `Retention`; `--retention`, WPF | Verificado por origen, calendario UTC y protección del último registro. |
| Suspender ante catálogos perdidos/dañados o copia pendiente | `AuditRetention`; `RetentionProbe.CheckFailures` | Verificado; cruza conjuntos esperados, confirmaciones y ambas réplicas. |
| Cuarentena, diario, deshacer y gracia desde finalización | `RetentionJournal`, `RetentionCleanup`; `--retention` | Verificado por transición, reanudación tardía y proceso terminado con origen ausente. |
| Purga posterior, nuevos referentes y coordinación con restauración | Bloqueo común, auditoría nueva y rescate de objetos | Verificado en `--retention`; no es reversible una vez iniciada la purga. |
| Retención con varias generaciones grandes | `--retention-scale` | Verificado: ocho × 4.096 archivos. Pendiente: varias generaciones de aproximadamente 329.000 rutas con recursos acotados y contadores. |
| Espejo navegable, altas/reemplazos/retiradas y conflictos | `MirrorPreview`, `MirrorExecution`, `MirrorJournal`; `--mirror`, WPF | Motor y flujo local aprobados. Confirmar en CI. |
| Origen ausente, parcial, vacío y cambios masivos en espejo | `MirrorProbe.CheckGuards` y WPF | Rechazo de origen ausente, sustituido o bloqueado y revisión adicional de vacío y cambios masivos verificados localmente. |
| Espejo coordinado con respaldo, restauración y GC | Registro replicado, bloqueo común y referencias al espejo | Verificado localmente con reanudación sin origen, conservación de la fecha anterior y limpieza bloqueada mientras queda una operación pendiente. |

## Entrega y comprobaciones externas

| Requisito | Evidencia actual | Pendiente |
|---|---|---|
| Distribución portable reproducible y runtime incluido | Workflow `copy-probe.yml`: dos árboles limpios, SHA256SUMS idénticos, WPF y Programador desde paquete | Repetir para el estado final, conservar artefacto y commit. |
| Instalación, configuración, copia, recuperación e incidencias | ALFA, VERSIONES y RETENCION | Unificar procedimiento de piloto y diagnóstico, incluir espejo, criterios de aceptación y detención y observación de 7–14 días. |
| Limpieza de repo, privacidad y conservación de cambios del usuario | Estado inicial limpio; AGENTS exige revisión previa a publicar | Revisar en cada hito y al cierre. Artefactos fuera del repositorio. |
| USB NTFS físico, nube real, 4Kn físico, otra cuenta/equipo, sesión, reinicio, suspensión, DPI y notificaciones | Solo ensayos sintéticos o definiciones donde se indica | Registrar como validación externa; no bloquear trabajo de software independiente ni declararlas superadas. |

El corte eléctrico físico, FreeFileSync y los respaldos personales están excluidos. Este goal prepara el piloto con datos sintéticos; no inicia observación ni migra el respaldo principal. Las funciones explícitamente posteriores del plan (VSS, watcher, conexión USB automática, NAS, cifrado y sincronización bidireccional) no se convierten en requisitos implícitos de estas etapas. Los deseables de beta que no sean gates de las etapas deben seguir identificados como tales.
