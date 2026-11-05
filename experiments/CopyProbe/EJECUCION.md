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

Versionado reintenta la generación pendiente o la generación registrada por el intento interrumpido. Acumulativo y espejo usan su diario de publicación. Reanudar un inventario persistido sigue exigiendo sus datos e identidades; un origen cambiado puede requerir revisión. El flujo de producto para abandonar o sustituir una intención incompatible conservando sus datos sigue pendiente. También siguen pendientes el traslado de la revisión inicial de espejo y de la restauración al flujo controlable, los horarios, la bandeja y el resto de la gestión de trabajos descrita en [V1.md](../../V1.md).

## Pruebas

`--product-runs` ejecuta los tres modos, registra verificaciones separadas, conserva la ruta de un archivo ocupado, ordena intentos al retroceder el reloj y prueba origen y destino ausentes y reconectados. Impide una ejecución concurrente del mismo trabajo y exige aprobación del espejo. Conserva un registro incompleto y reserva su secuencia al continuar.

El mismo arnés crea trabajadores reales en los tres modos. Solicita pausa, observa su bloqueo y progreso, continúa o cancela, y termina otro proceso mientras está pausado. Reabre la configuración y completa la misma generación. Rechaza controles atrasados de un intento anterior. La prueba WPF usa el ejecutor sin ventana en sus acciones de respaldo y verificación. Un trabajador de prueba retenido en un límite permite comprobar determinísticamente sus botones, el cierre de la ventana y la lectura del resultado al reabrir. Estas pruebas de procesos no sustituyen reinicio de Windows, cierre de sesión, suspensión ni desconexión física.
