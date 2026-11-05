# Piloto controlado de FolderTwin

El piloto de producto dura de 7 a 14 días. Usa Windows 11 x64, discos locales NTFS y carpetas desechables. Prepara archivos sintéticos que puedas volver a generar y reserva otra ubicación para las restauraciones. Conserva aparte los respaldos personales y FreeFileSync. La observación comienza cuando una persona inicia este procedimiento con un paquete cuya aceptación de software haya sido revisada. No se cuenta el tiempo transcurrido desde crear este documento. La vista previa todavía tiene requisitos de software abiertos en [V1.md](V1.md).

## Instalar y preparar

1. Desde el resultado de Windows del commit elegido, descarga `FolderTwin-alpha-win-x64-<commit>` y extrae el contenido completo a una carpeta nueva. El paquete incluye el runtime de .NET. Conserva el identificador del commit y `SHA256SUMS.txt` con el registro del piloto.
2. En el Explorador crea una carpeta desechable identificada como piloto, con subcarpetas de origen, destino y recuperaciones. Prepara un texto, un archivo vacío, una carpeta vacía, nombres Unicode y varios niveles. Guarda aparte una lista del contenido esperado. No necesitas crear un espacio de laboratorio ni abrir **Respaldos de alfa** para este recorrido.
3. Abre `FolderTwin.exe`. Desde **Nuevo trabajo**, crea un trabajo de **Copia acumulativa**, uno de **Versionado** y uno de **Espejo protegido**, con nombres distintos y el mismo origen sintético. Selecciona el destino padre desechable: cada trabajo creará una subcarpeta dedicada diferente. Revisa las selecciones y empieza a demanda. [Pasos de la interfaz](GUIA.md#crear-un-trabajo).
4. En cada trabajo usa **Respaldar ahora**, espera el resultado y abre **Recuperar archivos**. Elige la fecha, usa **Verificar respaldo**, recupera el punto completo en una carpeta nueva y comprueba el texto, nombres y carpeta vacía. Anota los resultados de cada modo por separado.
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

Detén nuevas ejecuciones del modo afectado ante un hash inesperado, un punto incompleto mostrado como confirmado, una retirada sin revisión, un destino equivocado o datos ajenos modificados. Retira el horario del trabajo afectado, conserva el espacio y registra el estado. Para cualquier incidencia incluye hora local y zona, commit, modo, acción, mensaje y resultado de verificación. Comparte solo nombres relativos sintéticos y elimina rutas de usuario, credenciales y datos privados del informe. Una réplica dañada puede permitir recuperación y a la vez suspender la limpieza. Espacio insuficiente, origen ausente y archivos en uso requieren resolver el problema antes del reintento.

## Validaciones fuera de la aceptación de software

| Entorno | Comprobación pendiente y evidencia esperada |
|---|---|
| VM Windows con una cuenta de prueba | Programa una hora y cierra sesión antes del disparador. Inicia sesión y registra hora esperada y real, recuperación del horario, historial, estado y una verificación. La bandeja opcional se abre desde la interfaz; no presupongas un inicio automático del monitor de producto. |
| VM con reinicio o suspensión controlados | Deja pasar una hora programada, vuelve a la sesión y registra si Windows recupera el horario, cuánto demora y si el punto se verifica. Ensaya reinicio abrupto de la VM por separado con una copia sintética pendiente. |
| Equipo de laboratorio con NTFS extraíble | Registra desconexión, retorno y disco sustituido. Comprueba que la identidad equivocada se rechaza, que el retorno permite verificar y que se conservan los incidentes. Registra geometría 512e o 4Kn y resultado de la lectura de prueba. |
| Nube de laboratorio | Usa archivos sintéticos locales, parcialmente descargados y solo en la nube. Comprueba el inventario, los avisos y que el ensayo no provoque hidratación involuntaria. |
| Otra cuenta o equipo de laboratorio | Exporta la configuración sintética, importa con revisión de la misma identidad de destino y **Recuperar sin origen**, y restaura sin índice previo. Registra permisos y cualquier límite de reasociación. No habilites un trabajo cuya identidad no puedas comprobar. |
| Escritorio interactivo de pruebas | Recorre instalación, creación de los tres modos, verificación, incidencia resuelta y recuperación sin consola. Comprueba Tab/Shift+Tab, foco, lector de pantalla, tema claro/oscuro, 100/150/200 % y cambios de DPI entre monitores disponibles. Anota los casos que no se pudieron probar. Comprueba entrega visible de avisos y respuesta de otras aplicaciones durante un respaldo en las configuraciones HDD/SSD y CPU disponibles. |

Cada validación externa conserva su propio resultado. Usa solo datos sintéticos. La aceptación de Windows en CI cubre arneses, terminaciones de proceso, tareas reales desde el paquete y reproducción de archivos distribuidos. El corte eléctrico físico queda excluido.

Para cada caso conserva: ID de V1 o EXT, commit y hash del paquete, inicio y fin con zona horaria, entorno, pasos, resultado esperado y observado, mensaje literal sin datos privados y verificación de los archivos restaurados. Usa capturas solo de la sesión sintética. Un caso sin esa evidencia permanece pendiente. Un error impide aprobar el caso y debe registrar el punto de reanudación después de corregirlo.

Consulta [V1.md](V1.md) para aceptar el producto y [ACEPTACION.md](ACEPTACION.md) para la evidencia anterior del motor. Este documento deja listo el procedimiento; no acredita una observación ya realizada.
