# Ejecución de trabajos de producto

El ejecutor WPF admite `--run-product-job <configuración> <trabajo>` y `--verify-product-job <configuración> <trabajo> <generación>`. El primer comando acepta además el GUID de una aprobación de espejo. Son puntos de entrada del ejecutor sin ventana; las acciones normales están en la interfaz. La configuración se valida por identidad antes de abrir un trabajo. Los ensayos usan carpetas desechables con datos sintéticos.

## Registro y estado

Cada trabajo conserva `execution.lock` y una carpeta `runs`. El bloqueo exclusivo serializa respaldo y verificación entre ventanas, procesos y futuros disparadores. Cada intento ocupa un directorio cuyo nombre contiene una secuencia decimal de 20 posiciones y un GUID. El siguiente intento reserva un número mayor incluso si un documento anterior quedó incompleto. Los registros ilegibles se conservan y se muestran como una incidencia del historial.

El intento guarda una intención con versión, trabajo, GUID de ejecución, secuencia, operación, fecha e identidad de la configuración confirmada. Guarda por separado la generación antes de ejecutarla y el resultado al terminar. Estos registros usan SHA-256 y publicación por temporal, flush, relectura y renombrado sin reemplazo. Los checksums detectan corrupción accidental y no autentican modificaciones de alguien con acceso a la configuración.

`active.lock` permanece abierto durante el intento. La interfaz comprueba ese bloqueo para distinguir un trabajador vivo de una intención interrumpida. Un archivo de estado o un PID guardado no bastan para declarar que está ejecutándose. Sin resultado válido ni bloqueo activo, el intento aparece interrumpido. Para informar éxito hace falta el resultado durable de una operación que haya pasado las comprobaciones del motor.

Los resultados conservan fecha, estado, generación, ruta relativa de incidencia, código de error y un detalle local acotado. La secuencia ordena los intentos cuando cambia el reloj. Las verificaciones explícitas tienen su propia operación y fecha; no actualizan la fecha del último respaldo completo. La interfaz indica también la fecha del punto recuperable y del punto verificado, porque completar o verificar un punto anterior no crea una instantánea nueva del origen.

## Progreso y control

El progreso alterna dos archivos con checksum y revisión creciente. Se actualiza con límite de frecuencia durante las fases de análisis, copia, verificación y publicación. Es información orientativa, no una confirmación de datos ni autoridad para reanudar. Una escritura parcial o un error de acceso al progreso no autoriza a mostrar un respaldo completo. El resultado durable sigue siendo obligatorio.

Las solicitudes numeradas de pausa, continuación y cancelación quedan ligadas al hash de la intención de ejecución. Los controles de un intento terminado no se aplican a otro. El trabajador las consulta en límites del motor entre archivos y pasos recuperables. Una solicitud puede esperar a que termine la lectura o transferencia del archivo actual. La interfaz muestra pausa efectiva cuando el trabajador la confirma. Una cancelación preserva los planes y los datos existentes; no termina el proceso por la fuerza.

La copia y la verificación se ejecutan en otro proceso. Cerrar la ventana detiene su consulta de estado y libera sus handles locales, sin terminar ese proceso. Al reabrir, la interfaz reconstruye estado e incidencias desde la configuración. El historial muestra las operaciones anteriores por trabajo, incluidos fallos que precedieron a un intento correcto.

## Espejo y reintento

Una ejecución sin aprobación deja pendientes las retiradas de archivos o carpetas, los cambios masivos y el origen vacío. La aprobación manual conserva generación, hashes de catálogo y registro, identidades, punto anterior, umbral y aceptaciones. El ejecutor vuelve a revisar y exige que coincidan antes de aplicar. Una actualización ya pendiente se reanuda desde su diario autorizado.

Versionado reintenta la generación pendiente o la generación registrada por el intento interrumpido. Acumulativo y espejo usan su diario de publicación. Reanudar un inventario persistido sigue exigiendo sus datos e identidades; un origen cambiado puede requerir revisión. El producto permite revisar y conservar un plan completo de versionado antes de iniciar otro. Para borradores y diarios de los otros modos, se retira el trabajo conservando sus datos y recuperación y se crea otro destino dedicado. El diario anterior y su espacio permanecen; no se completan con otro contenido. [Procedimiento y límites](PENDIENTES.md).

## Revisión controlada del espejo

`--review-product-mirror` ejecuta el análisis con una operación `ReviewMirror` independiente. Mantiene el bloqueo por trabajo, publica progreso y atiende las mismas solicitudes de pausa y cancelación. El motor mantiene su bloqueo del almacén mientras analiza. Preparar el almacén y la carpeta actual vacía puede ser necesario en la primera ejecución, pero el análisis no publica puntos ni actualiza archivos de la carpeta actual.

El intento conserva una generación propuesta y un resumen acotado en `mirror-review`: hash de intención, prueba de revisión, hashes de documento y registro, identidades, punto anterior, conteos de archivos y carpetas, conflictos, espacio observado y aceptaciones requeridas. No persiste el inventario completo en la configuración local. El resultado confirmado significa que terminó el análisis de esa operación; no actualiza la fecha del respaldo ni aparece como punto recuperable. Un resumen sin resultado confirmado no habilita aplicar.

La interfaz reabre la revisión desde el mismo intento. Aplicar guarda una aprobación nueva ligada a esa revisión y exige las aceptaciones de origen vacío o cambios masivos. El ejecutor vuelve a analizar con la generación propuesta y comprueba hashes, identidades y punto anterior antes de actualizar. Los conflictos impiden aprobar. Una revisión obsoleta no permite cambiar datos. Si ya hay un diario autorizado pendiente, la revisión lo identifica y ofrece reanudar su operación en lugar de aprobar un análisis nuevo.

Terminar o cancelar el proceso de análisis conserva los registros y los respaldos anteriores. El análisis se repite para obtener una nueva revisión. La bandeja conserva las incidencias de copia y también señala análisis fallidos o cancelados y revisiones destructivas pendientes. Una revisión correcta no oculta un fallo anterior del respaldo. `--product-mirror-review` y el recorrido WPF prueban estos límites, controles reales, terminación, reapertura y aplicación separada. La aceptación visual y las validaciones externas siguen separadas.

## Restauración controlada

`--restore-product-job` recibe configuración, trabajo y GUID de revisión. La revisión inmutable en `restores` conserva generación, archivo opcional, origen del respaldo, carpeta padre de recuperación, identidades, carpeta nueva e identidad del registro del trabajo. El intento enlaza esa revisión y registra `Restore` como operación propia; las intenciones anteriores conservan su serialización y hashes. El resultado y el historial incluyen la salida revisada. Ni una restauración correcta ni su verificación final actualizan las fechas de respaldo o verificación explícita.

Antes de escribir se vuelven a comprobar la revisión, las raíces conocidas, el marcador y el estado retenido de la generación. El destino nuevo queda fuera de la configuración y de los orígenes y destinos de todos los trabajos registrados. Durante la recuperación, la reserva del intento impide registrar otro trabajo que use su salida. El bloqueo por trabajo excluye copia y verificación; el bloqueo del almacén coordina también con la limpieza y la recuperación independiente.

El motor informa verificación de objetos, creación de carpetas, copia y verificación final. Pausa y cancelación se atienden entre esos pasos. Cerrar WPF conserva el trabajador. Al reabrir se consulta el mismo intento y su revisión. Cancelar o terminar el proceso no elimina ni adopta la salida parcial: para repetir se revisa una carpeta nueva. Un intento interrumpido no se declara completo por encontrar archivos en ella. La bandeja conserva las incidencias del respaldo aunque una restauración posterior termine correctamente.

`--product-restore` prueba los tres modos con origen ausente, archivo elegido, revisión inválida, carpeta padre sustituida y trabajo retirado. Usa trabajadores reales para pausa, continuación, cancelación y terminación, y prueba el reintento en una carpeta distinta. Durante la pausa rechaza copia, limpieza y registro de un trabajo que use la salida. WPF añade controles visibles en ambas vistas, cierre de ventana y reapertura del resultado; estas comprobaciones no sustituyen la aceptación visual ni las pruebas externas.

## Configuración y horario

Las revisiones de nombre, hora, habilitación y retirada se publican sin reemplazo en `settings`, con secuencia, checksum, hash anterior e identidad del registro original. No cambian las raíces, el modo ni la identidad usada por resultados y aprobaciones. Detectan corrupción y huecos interiores; no constituyen un registro autenticado contra restaurar una copia antigua de toda la configuración. Retirar conserva las versiones y permite verificar y recuperar; bloquea nuevos respaldos. Cambiar carpetas o modo requiere crear otro trabajo.

El ejecutor estable de producto usa un perfil separado del laboratorio. El Programador usa el usuario interactivo, privilegio normal, `StartWhenAvailable`, `IgnoreNew` y ninguna terminación forzada por plazo. Antes de actualizar o retirar una tarea comprueba descripción, usuario, acción y ejecutable instalado. El calendario se compara también con la hora configurada. Una tarea ajena se conserva para revisión. La interfaz permite reparar un horario pendiente y solo muestra la próxima fecha nativa comprobada. Reinicio, suspensión y sesión siguen pendientes de la matriz externa.

`--run-scheduled-product-job` mantiene `scheduled.lock` durante toda su invocación. Reintenta hasta tres veces, a intervalos de cinco minutos, únicamente tras origen o destino ausente o archivo ocupado. Libera el bloqueo de ejecución entre intentos. El registro de reintento conserva la intención, número y próxima fecha; `retry.active` distingue una espera viva de un archivo antiguo. Durante la espera revisa habilitación y propiedad de la tarea cada cinco segundos. Deshabilitar, retirar o pasar a demanda elimina primero la tarea propia; si esa comprobación falla no guarda la desactivación. No vuelve a crear automáticamente una tarea eliminada al abrir la ventana.

El arnés local inyecta la espera y las operaciones de programación del recorrido WPF. `--product-schedule-smoke` reserva los disparadores nativos, la instalación estable y la espera real al runner Windows desechable. Comprueba los tres modos, cambio de hora, destino ausente y reconectado, exclusión durante la espera, conservación de la incidencia y desactivación de un reintento. Su resultado se registra separado de las pruebas locales.

## Pruebas

`--product-runs` ejecuta los tres modos, registra verificaciones separadas, conserva la ruta de un archivo ocupado, ordena intentos al retroceder el reloj y prueba origen y destino ausentes y reconectados. Impide una ejecución concurrente del mismo trabajo y exige aprobación del espejo. Conserva un registro incompleto y reserva su secuencia al continuar.

El mismo arnés crea trabajadores reales en los tres modos. Solicita pausa, observa su bloqueo y progreso, continúa o cancela, y termina otro proceso mientras está pausado. Reabre la configuración y completa la misma generación. Rechaza controles atrasados de un intento anterior. La prueba WPF usa el ejecutor sin ventana en sus acciones de respaldo y verificación. Un trabajador de prueba retenido en un límite permite comprobar determinísticamente sus botones, el cierre de la ventana y la lectura del resultado al reabrir. Estas pruebas de procesos no sustituyen reinicio de Windows, cierre de sesión, suspensión ni desconexión física.
