# Retención experimental

Se habilita para almacenes de laboratorio `FTVERS02` y trabajos de producto `FTVERS03` con registro de generaciones comprobable. `FTVERS01` conserva recuperación, pero no autoriza limpieza. Las operaciones son manuales y comparten el bloqueo exclusivo de copia, verificación y restauración. Los ensayos usan datos sintéticos; `--retention` ejecuta el arnés desechable y `--product-smoke` recorre los controles de producto.

## Revisión

La propuesta inicial conserva un punto por día durante 30 días, por semana ISO durante 8 semanas y por mes durante 12 meses. Usa calendario UTC y une los tres conjuntos. Conserva el último punto registrado de cada identidad de origen, aunque su fecha sea anterior por un cambio del reloj. Cada origen tiene su propio calendario; compartir almacén no mezcla las fechas de trabajos distintos. Un origen retirado conserva su último punto. Los intervalos y la gracia se pueden cambiar antes de aplicar la revisión. Los mínimos son un día diario y un día de gracia. Se suspende la selección si hay fechas posteriores al reloj actual.

Antes de proponer retiradas se comprueban ambas réplicas del registro, el conjunto exacto de confirmaciones y manifiestos, las dos copias de cada catálogo esperado, sus hashes registrados y los descriptores que fijan las identidades de origen. La pérdida de las dos réplicas de un catálogo no hace desaparecer la expectativa. Una copia sin confirmar, un temporal desconocido, una réplica ausente, un conflicto o un objeto activo que no verifica impiden la limpieza. La recuperación ordinaria desde una sola réplica sigue disponible.

Se calcula la unión de objetos de todos los puntos que seguirán retenidos. Solo son candidatos los objetos de puntos que expirarán en esta operación y que no pertenecen a esa unión. Los archivos extraños u objetos sin procedencia demostrada se conservan para revisión. Un cambio del registro después de la vista previa invalida su aplicación. La política no autoriza limpieza automática.

## Diario y cuarentena

Cada operación ocupa `quarantine/<guid>/`. Su archivo inmutable `plan` contiene 32 bytes de SHA-256 seguidos del JSON UTF-8: versión, identidad y marcador del almacén, ID y revisión con política, hash del registro, generaciones conservadas/expiradas y candidatos. El límite es 128 MiB. Los temporales se vacían, releen y publican por renombrado sin reemplazo. Un temporal contradictorio se conserva y bloquea la operación.

Después de guardar el plan se registran las expiraciones en ambas réplicas. El diario permite completar únicamente el evento pendiente exacto; un prefijo común del registro nunca autoriza por sí solo borrados. Se revisan de nuevo los catálogos y todas las referencias activas antes de mover objetos. Cada candidato se abre por un handle relativo al directorio fijado, verifica tamaño y SHA-256 y se renombra dentro del volumen a `quarantine/<guid>/<sha256>`. El plan conserva su ruta original `objects/sha256/<prefijo>/<sha256>`. Los estados del conjunto no son una transacción atómica; cada movimiento se reanuda observando ambas ubicaciones. Ausencia o duplicación contradictoria requieren revisión.

`quarantined-at` liga al plan la fecha UTC de finalización con un checksum. La gracia empieza al terminar los movimientos, incluso si la ejecución se reanuda semanas después de crear el plan. Los marcadores `quarantined`, `undoing`, `returned`, `undone`, `purging` y `purged` contienen hashes ligados al plan. Se conservan los manifiestos, descriptores y diarios después de la purga para no perder las expectativas ni el historial. Esta versión no compacta esos metadatos ni recoge objetos de procedencia desconocida.

Un nuevo respaldo recupera y verifica un objeto de cuarentena antes de publicar referencias a él. Deshacer devuelve los objetos a CAS, verifica los puntos y registra su conservación antes de confirmar `undone`. Se puede reanudar un deshacer interrumpido. Las fechas expiradas siguen siendo navegables, pero la interfaz exige deshacer su cuarentena antes de restaurarlas normalmente.

## Purga

Es una invocación posterior y explícita. Exige cuarentena terminada, al menos la gracia configurada (siete días por defecto), ausencia de operaciones pendientes y una nueva comprobación de catálogos, referencias y objetos activos. El reloj no puede preceder a las generaciones conocidas ni a la finalización registrada. Un nuevo respaldo correcto no sustituye esta revisión. El reloj del equipo sigue siendo la fuente de tiempo; esto no acredita tiempo físico frente a cambios deliberados del reloj.

Los candidatos nuevamente referenciados se conservan en CAS. Un objeto activo que siga en cuarentena detiene la purga para reparar su ubicación. Antes de empezar se verifican todos los candidatos restantes. Tras guardar `purging`, cada objeto se verifica por su handle exclusivo y se solicita su eliminación al cerrarlo mediante `FileDispositionInformation`. Una interrupción se reanuda desde el plan y las ubicaciones observadas. Se conserva el recibo anterior de copia; si el contenido purgado vuelve a necesitarse, el diario autoriza una copia nueva con recibo propio de la generación.

La purga no se puede deshacer. Faltas, corrupción del diario y estados contradictorios suspenden la operación. Los hashes detectan corrupción accidental; no autentican cambios de quien pueda reescribir datos y metadatos. No se promete recuperación tras pérdida del dispositivo, de datos o del diario. [Contrato de la API de disposición](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ntifs/nf-ntifs-ntsetinformationfile).

## Evidencia y límites

El arnés cubre interrupciones de planificación, expiración, movimientos, finalización, devolución y purga; reparación de un segundo evento de registro parcialmente escrito; archivos compartidos; varios orígenes; plazo de gracia y reanudación tardía; nueva referencia a un objeto de cuarentena; recopia de contenido purgado; catálogos ausentes/dañados, planes obsoletos, copias pendientes y corrupción del diario y objetos. Termina un trabajador real durante un movimiento y otra instancia reanuda y deshace con el origen ausente. La prueba WPF recorre revisión, aceptación, expiración visible, deshacer, rechazo de purga anticipada y reapertura del historial.

Son pruebas de software sobre datos sintéticos. El ensayo de reinicio abrupto de Windows y los dispositivos USB reales quedan como validaciones externas. El corte eléctrico físico queda excluido. La auditoría procesa un catálogo primario por generación, compara por SHA-256 su réplica y la lista asociada al descriptor, y conserva las referencias de objetos en lugar de los árboles completos. La memoria depende del número de objetos únicos y sus referencias entre generaciones.

`--retention-scale` genera ocho puntos de 4.096 archivos cada uno, con contenidos compartidos y un cambio por fecha. Aparta el origen antes de revisar, expirar cinco puntos y purgar sus cinco objetos exclusivos; verifica los tres puntos conservados. En una ejecución local sobre Windows y .NET 10.0.14, la revisión tardó 0,496 s, la cuarentena 1,092 s y la purga 0,460 s. La E/S contabilizada por proceso fue 20.542.738 / 49.709.974 / 20.549.560 bytes leídos y 0 / 5.367 / 64 bytes escritos. CPU: 0,469 / 1,266 / 0,484 s. El máximo de memoria residente observado del proceso completo, incluida la preparación, fue 88.858.624 bytes. Son contadores de Windows, incluyen cachés y metadatos, y no miden transferencias físicas ni respuesta de otras aplicaciones. Este tamaño no acredita muchas generaciones de 329.000 archivos.

El workflow **Synthetic retention scale** usa tres generaciones reales de 329.000 archivos, cada una con un cambio de contenido y referencias compartidas. Limita la ejecución a 45 minutos en un runner hospedado desechable y comprueba la capacidad antes de crear datos. Aparta el origen, conserva dos fechas, mueve y purga el objeto exclusivo de la fecha expirada y verifica los dos puntos retenidos. La primera ejecución registró 148,975 s de preparación y 83,097 / 79,865 / 80,475 s para los tres respaldos. El máximo residente acumulado fue 1.108.574.208 bytes. [Ejecución de referencia](https://github.com/mikepchelper-spec/FolderTwin/actions/runs/41957038098).

| Fase de referencia | Tiempo (s) | CPU (s) | Bytes leídos | Bytes escritos | Máximo residente acumulado (bytes) |
|---|---:|---:|---:|---:|---:|
| Revisión | 11,783 | 11,781 | 657.167.042 | 0 | 571.879.424 |
| Cuarentena | 26,486 | 26,312 | 1.460.382.972 | 1.580 | 936.005.632 |
| Purga | 11,716 | 11,703 | 657.170.270 | 64 | 1.108.574.208 |

Estos datos motivaron procesar catálogos de uno en uno y guardar referencias únicas por generación. El ensayo mide el patrón de contenidos compartidos descrito. Una colección con más objetos únicos o más generaciones puede consumir más memoria y E/S.

La variante optimizada volvió a conservar dos puntos y purgar solo el objeto exclusivo con el origen ausente. La preparación tomó 93,181 s y los respaldos 66,205 / 58,826 / 58,939 s. El máximo residente del proceso completo bajó a 501.125.120 bytes en la segunda ejecución. Los runners hospedados pueden tener distinta carga. Los tiempos son observaciones de cada ejecución. [Ejecución optimizada](https://github.com/mikepchelper-spec/FolderTwin/actions/runs/41958434672).

| Fase optimizada | Tiempo (s) | CPU (s) | Bytes leídos | Bytes escritos | Máximo residente acumulado (bytes) |
|---|---:|---:|---:|---:|---:|
| Revisión | 3,232 | 3,250 | 657.181.457 | 0 | 501.125.120 |
| Cuarentena | 8,048 | 8,094 | 1.460.415.140 | 1.580 | 501.125.120 |
| Purga | 3,168 | 3,156 | 657.185.489 | 64 | 501.125.120 |
