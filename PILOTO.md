# Piloto controlado de FolderTwin

Disponibilidad de entorno, 5 de noviembre de 2026: no hay por ahora USB, otra cuenta/equipo o VM de pruebas disponibles. El piloto no se ha iniciado. Sus validaciones externas permanecen pendientes por indicación del usuario; no se les asigna una fecha ni se dan por aprobadas.

El piloto de producto dura de 7 a 14 días. Usa Windows 11 x64, discos locales NTFS y carpetas desechables. Prepara archivos sintéticos que puedas volver a generar y reserva otra ubicación para las restauraciones. Conserva aparte los respaldos personales y FreeFileSync. La observación comienza cuando una persona inicia este procedimiento con un paquete cuya aceptación de software haya sido revisada. No se cuenta el tiempo transcurrido desde crear este documento. [ENTREGA.md](ENTREGA.md) fija el candidato comprobado y su evidencia; el [VHDX de 76b7eed](https://github.com/mikepchelper-spec/FolderTwin/actions/runs/42105791375) está aprobado. La [matriz de v1](V1.md) conserva las validaciones externas pendientes.

## Instalar y preparar

1. Desde el resultado de Windows del commit elegido, descarga `FolderTwin-win-x64-<commit>` y extrae el contenido completo a una carpeta nueva. Guarda el ZIP descargado, el identificador del commit y `SHA256SUMS.txt` con el registro del piloto. El paquete incluye el runtime de .NET. Conserva la misma distribución durante la observación y registra cualquier cambio de compilación.
2. En el Explorador crea una carpeta desechable identificada como piloto, con subcarpetas de origen, destino y recuperaciones. Prepara un texto, un archivo vacío, una carpeta vacía, nombres Unicode y varios niveles. Guarda aparte una lista del contenido esperado. No necesitas crear un espacio de laboratorio ni abrir **Respaldos de alfa** para este recorrido.
3. Abre `FolderTwin.exe`. Desde **Nuevo trabajo**, crea un trabajo de **Copia acumulativa**, uno de **Versionado** y uno de **Espejo protegido**, con nombres distintos y el mismo origen sintético. Selecciona el destino padre desechable: cada trabajo creará una subcarpeta dedicada diferente. Revisa las selecciones y empieza a demanda. [Pasos de la interfaz](GUIA.md#crear-un-trabajo).
4. En cada trabajo usa **Respaldar ahora**. Para espejo, lee la revisión y pulsa **Aplicar espejo revisado**. Cuando el respaldo esté completo, abre **Recuperar archivos**, elige la fecha y usa **Verificar respaldo**. Recupera el punto completo en una carpeta nueva y comprueba el texto, los nombres y la carpeta vacía. Anota los resultados por modo.
5. Modifica un texto, agrega otro archivo y elimina uno del origen. Repite el respaldo y la verificación en los tres modos. En espejo revisa y aplica los cambios autorizados. En versionado compara las dos fechas y recupera el archivo eliminado desde la vista de cambios. Comprueba que acumulativo conserva el archivo desaparecido del origen. Usa una salida distinta para cada recuperación.

Los enlaces, datos solo en la nube detectados, EFS y flujos no soportados generan incidencias. El origen puede cambiar entre lecturas. Las copias representan los estados observados y las verificaciones comparan bytes y metadatos admitidos; no son una instantánea coherente de una aplicación abierta. La [guía de uso](GUIA.md) y la matriz delimitan lo soportado.

## Operar y recuperar

- En **Editar trabajo y horario**, escribe una hora local HH:mm, habilita el trabajo y pulsa **Guardar cambios**. Comprueba el horario observado; si requiere atención usa **Reparar horario**. La aplicación instala un ejecutor verificado en una carpeta estable del usuario. El modo actual requiere sesión iniciada. Cierra la interfaz, espera la hora y vuelve a abrirla. Revisa cada historial y realiza una verificación explícita. Un horario reintenta hasta tres veces, cada cinco minutos, ante una raíz ausente o un almacén ocupado.
- Usa **Minimizar a bandeja** para consultar los trabajos, ejecutar una copia y **Abrir aplicación**. Registra avisos, antigüedad y estado por trabajo. La entrega de avisos y el comportamiento real de la sesión se observan por separado; este recorrido no ofrece un ajuste de arranque del monitor al iniciar sesión.
- En espejo revisa altas, reemplazos, retiradas, conflictos y espacio antes de **Aplicar espejo revisado**, incluidos los consentimientos adicionales cuando correspondan. Una actualización autorizada pendiente ofrece **Reanudar actualización**. Conserva sus carpetas y respeta el estado de origen indicado por el mensaje. No borres diarios para empezar otro intento.
- En **Retención manual y cuarentena**, revisa días, semanas, meses y gracia y registra qué fechas se conservarán. Prueba **Mover a cuarentena** y **Deshacer cuarentena**. Para comprobar una purga, usa otra operación desechable, espera la gracia real desde su finalización, verifica un punto retenido y acepta el borrado irreversible. La cuarentena no libera todavía espacio. Mantén la limpieza manual.
- En **Recuperar archivos**, elige fecha y alcance y revisa siempre una salida nueva. Si un punto expiró y su cuarentena es reversible, deshazla antes de recuperar. Los respaldos de formatos anteriores tienen su [procedimiento separado](COMPATIBILIDAD.md); las pruebas del laboratorio no sustituyen este recorrido de producto.
- Durante el ensayo prueba una pausa, continuación, cancelación y reintento con un lote sintético suficiente para observarlos. Cierra y reabre la interfaz con un trabajador activo. Registra el intento y el resultado, conservando los parciales. Si el origen ya no coincide con un plan, usa únicamente la resolución documentada para su modo; los casos pendientes de implementación no se dan por aprobados.

## Observación y decisión

| Cuándo | Qué registrar |
|---|---|
| Inicio | Commit, edición de Windows, tipo de volumen, espacio libre, horario y modos que se ensayarán. Guarda el resultado de la primera copia, la primera verificación y una restauración. |
| Cada día | Hora esperada y real, resultado por trabajo, antigüedad del último punto, espacio libre, incidencias y pasos necesarios para resolverlas. Distingue intento fallido de punto confirmado. |
| Al menos dos veces | Restaura archivos distintos, incluido uno eliminado o reemplazado, en carpetas vacías. Comprueba contenido y metadatos soportados. Registra fecha, resultado y tamaño. |
| Con una incidencia sintética planificada | Cierra un archivo ocupado o devuelve la carpeta de destino registrada y reintenta. Comprueba que el historial mantiene la incidencia y registra el punto posterior. Usa solo el espacio desechable y espera el cierre del trabajador antes de mover una carpeta. |
| Cierre | Verifica los puntos retenidos elegidos, revisa el espejo y anota pendientes. Deshabilita o retira los trabajos desde su configuración; para una retirada global usa **Aplicación → Retirar todos los horarios** con su aceptación. Conserva evidencia y salidas sintéticas necesarias para revisar fallos. |

El piloto supera el criterio si todos los modos incluidos producen y recuperan los datos sintéticos esperados, las incidencias se explican y los intentos fallidos conservan los puntos anteriores. Registra cada caso externo como aprobado, fallido o pendiente con el equipo y la acción probada.

Para comprobar un reintento sin consola, espera a que todos los trabajos de prueba estén inactivos. En el Explorador renombra la carpeta sintética `origen` a `origen temporalmente ausente`. Selecciona el trabajo de versionado y pulsa **Respaldar ahora**. Comprueba que informa origen ausente y conserva la fecha del último respaldo completo. Devuelve a la misma carpeta el nombre `origen` y pulsa **Reintentar respaldo**. Verifica el punto y consulta **Historial e incidencias** para registrar ambos resultados.

Detén nuevas ejecuciones del modo afectado ante un hash inesperado, un punto incompleto mostrado como confirmado, una retirada sin revisión, un destino equivocado o datos ajenos modificados. Retira el horario del trabajo afectado, conserva el espacio y registra el estado. Para cualquier incidencia incluye hora local y zona, commit, modo, acción, mensaje y resultado de verificación. Comparte solo nombres relativos sintéticos y elimina rutas de usuario, credenciales y datos privados del informe. Una réplica dañada puede permitir recuperación y a la vez suspender la limpieza. Espacio insuficiente, origen ausente y archivos en uso requieren resolver el problema antes del reintento.

## Validaciones fuera de la aceptación de software

| Caso | Entorno | Evidencia esperada |
|---|---|---|
| EXT-02 | Equipo de laboratorio con NTFS extraíble | Registra desconexión, retorno con otra letra y disco sustituido. Comprueba que se rechaza la identidad equivocada, que el retorno permite verificar y que se conservan las incidencias. Registra geometría 512e o 4Kn y resultado de la lectura de prueba. |
| EXT-03A | Cuenta de prueba en Windows | Cierra sesión antes del disparador. Al volver, registra hora esperada y real, estado, historial y verificación del punto. Abre la bandeja desde la interfaz. |
| EXT-03B | VM con reinicio controlado | Reinicia y deja pasar una hora programada antes de volver a la sesión. Registra la demora, el estado del horario y el resultado de verificar. Para el reinicio abrupto, usa una copia sintética pendiente en una VM y registra el resultado en otra entrada. |
| EXT-03C | Windows de prueba con suspensión | Suspende antes de la hora programada y reanuda después. Registra si se recupera el disparador, cuánto demora y si el punto verifica. |
| EXT-04A | Otra cuenta o equipo de laboratorio | Exporta la configuración sintética, revisa la misma identidad de destino al importar y elige **Recuperar sin origen**. Restaura sin índice previo. Registra permisos y cualquier límite de reasociación. |
| EXT-04B | Proveedor de nube de laboratorio | Usa archivos sintéticos locales, parcialmente descargados y solo en la nube. Comprueba inventario, avisos y estado de descarga antes y después. |
| EXT-05A | Escritorio interactivo de pruebas | Recorre asistente, trabajos, recuperación e incidencias con Tab, Shift+Tab y flechas. Registra el foco visible y los avisos en los temas claro y oscuro. Observa la respuesta durante un respaldo en las configuraciones HDD/SSD y CPU disponibles. |
| EXT-05B | Escritorio con lector de pantalla | Registra lector y versión, nombres anunciados, orden de navegación y avisos de revisión, error y recuperación. Prueba la lista vacía y los diálogos de origen y destino. |
| EXT-05C | Escritorio con escalado de Windows | Repite las pantallas principales al 100, 150 y 200 %. Registra controles inaccesibles o recortados y la navegación al mover la ventana entre monitores con distinto DPI, si están disponibles. |
| EXT-06 | Perfil limpio de Windows 11 x64 | Extrae la distribución, abre `FolderTwin.exe` desde el Explorador y sigue **Instalar y preparar**. Registra la creación de los tres modos, verificación, una incidencia resuelta y recuperación en una carpeta nueva. |

Cada validación externa conserva su propio resultado. Usa solo datos sintéticos. La aceptación de Windows en CI cubre arneses, terminaciones de proceso, tareas reales desde el paquete y reproducción de archivos distribuidos. El corte eléctrico físico queda excluido.

Para cada caso conserva: ID de V1 o EXT, commit y hash del paquete, inicio y fin con zona horaria, entorno, pasos, resultado esperado y observado, mensaje literal sin datos privados y verificación de los archivos restaurados. Usa capturas solo de la sesión sintética. Un caso sin esa evidencia permanece pendiente. Un error impide aprobar el caso y debe registrar el punto de reanudación después de corregirlo.

Consulta [V1.md](V1.md) para aceptar el producto y [ACEPTACION.md](ACEPTACION.md) para la evidencia anterior del motor. Este documento deja listo el procedimiento; no acredita una observación ya realizada.
