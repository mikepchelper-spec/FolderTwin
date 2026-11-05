# Retención experimental

Solo se habilita para almacenes `FTVERS02` dentro de espacios registrados con datos sintéticos. Las operaciones son manuales y comparten el bloqueo exclusivo de copia, verificación y restauración. `--retention` ejecuta el arnés desechable completo.

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

Son pruebas de software sobre datos sintéticos. Faltan el ensayo de reinicio abrupto de Windows, dispositivos USB reales y cargas prolongadas con muchas generaciones. El corte eléctrico físico queda excluido. La comprobación de retención carga los catálogos esperados; hay que medir memoria y duración en el conjunto previsto para el piloto antes de recomendar una retención extensa.
