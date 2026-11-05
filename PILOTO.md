# Piloto controlado de FolderTwin

El piloto dura de 7 a 14 días. Usa Windows 11 x64, un volumen local NTFS y un espacio de pruebas creado por FolderTwin. Prepara archivos sintéticos que puedas volver a generar y deja las restauraciones en subcarpetas vacías del mismo espacio. Conserva aparte los respaldos personales y FreeFileSync. La observación comienza cuando una persona inicia este procedimiento con el paquete del commit aceptado.

## Instalar y preparar

1. Desde el resultado de Windows del commit elegido, descarga `FolderTwin-alpha-win-x64-<commit>` y extrae el contenido completo a una carpeta nueva. El paquete incluye el runtime de .NET. Conserva el identificador del commit y `SHA256SUMS.txt` con el registro del piloto.
2. Abre `FolderTwin.exe` y **Respaldos de alfa** para este recorrido de laboratorio. Pulsa **Nuevo espacio de pruebas** y **Ver carpeta de pruebas**. Windows conserva ese espacio bajo `%LocalAppData%\FolderTwin-Lab\workspaces`. Crea subcarpetas directas para origen, copia inicial, almacén de versiones y salidas de restauración. Coloca en el origen archivos con nombres fáciles de reconocer, un archivo vacío, una carpeta vacía, nombres Unicode y una subcarpeta con varios niveles. El piloto de producto de v1 debe usar además los tres trabajos del panel según [V1.md](V1.md), sin sustituir su matriz por este ensayo anterior.
3. En la ventana principal, elige origen y destino de copia. **Analizar carpetas** muestra el alcance y el espacio. Ejecuta **Copiar y verificar**, **Verificar respaldo** y **Restaurar y verificar**. Abre algunos archivos restaurados, confirma sus nombres y comprueba que sigue presente la carpeta vacía.
4. En **Versiones**, crea el almacén en una carpeta vacía. Elige el origen sintético y crea el primer punto. Cambia un archivo, agrega otro y elimina uno. Crea un segundo punto, revisa los cambios y recupera el eliminado en una salida nueva. Verifica el punto y anota ambas fechas.

El asistente de copia conserva los metadatos declarados en ALFA. Los enlaces, datos solo en la nube detectados, EFS y flujos no soportados generan incidencias. El origen puede cambiar entre lecturas. Las copias representan los estados observados y las verificaciones comparan bytes y metadatos admitidos.

## Operar y recuperar

- En **Horario y bandeja**, escribe una hora local en formato HH:mm y pulsa **Programar**. La aplicación instala el ejecutor verificado en una carpeta estable del usuario. El modo actual requiere una sesión iniciada. Cierra la interfaz y vuelve a abrir el espacio tras la hora elegida. Revisa el historial, la antigüedad y el resultado de verificación. Un horario reintenta hasta tres veces, cada cinco minutos, si falta una raíz o el almacén está ocupado. Vuelve a pulsar **Programar** al actualizar una instalación anterior. También puedes usar **Copiar en segundo plano** o **Respaldar ahora** desde la bandeja.
- Usa **Minimizar a bandeja** para consultar los trabajos y abrir la aplicación. Si quieres comprobar el monitor al iniciar sesión en una VM preparada, activa **Mostrar estado al iniciar sesión** y pulsa **Aplicar inicio**. La bandeja avisa de incidencias y muestra la antigüedad del último punto consultable. Para comprobar todos sus objetos, abre el punto y pulsa **Verificar punto**.
- En **Espejo protegido**, prepara la carpeta actual, selecciona el origen y revisa altas, reemplazos, retiradas, conflictos y espacio. Aplica la revisión, incluidos los consentimientos adicionales cuando correspondan. Abre **Ver carpeta actual** y recupera un archivo anterior desde la fecha que lo conserva. Una actualización pendiente se continúa desde **Reanudar espejo**. Conserva las carpetas de la operación y respeta el estado del origen solicitado por el mensaje.
- En **Retención y cuarentena**, revisa los días, semanas, meses y gracia. Registra qué fechas se conservarán. Con un conjunto desechable, prueba **Mover a cuarentena** y **Deshacer cuarentena**. Para comprobar una purga en este piloto, usa una operación distinta con datos desechables, espera la gracia real desde la finalización, verifica un punto retenido y revisa la aceptación de borrado. Mantén la limpieza manual durante todo el piloto.
- Para restaurar una copia inicial o un punto, elige siempre una salida vacía distinta. Si un punto expiró y su cuarentena es reversible, deshazla antes de recuperar. El lector de recuperación independiente y sus argumentos se documentan en el almacén experimental. [Procedimiento de recuperación independiente](experiments/CopyProbe/VERSIONES.md).

## Observación y decisión

| Cuándo | Qué registrar |
|---|---|
| Inicio | Commit, edición de Windows, tipo de volumen, espacio libre, horario y modos que se ensayarán. Guarda el resultado de la primera copia, la primera verificación y una restauración. |
| Cada día | Hora esperada y real, resultado por trabajo, antigüedad del último punto, espacio libre, incidencias y pasos necesarios para resolverlas. Distingue intento fallido de punto confirmado. |
| Al menos dos veces | Restaura archivos distintos, incluido uno eliminado o reemplazado, en carpetas vacías. Comprueba contenido y metadatos soportados. Registra fecha, resultado y tamaño. |
| Con una incidencia sintética planificada | Cierra un archivo ocupado o devuelve la carpeta de destino registrada y reintenta. Comprueba que el historial mantiene la incidencia y registra el punto posterior. Usa solo el espacio desechable y espera el cierre del trabajador antes de mover una carpeta. |
| Cierre | Verifica los puntos retenidos elegidos, revisa el estado del espejo y anota pendientes. Usa **Quitar horario** y desactiva **Mostrar estado al iniciar sesión** si terminaron las pruebas. Conserva evidencia y salidas sintéticas necesarias para revisar fallos. |

El piloto supera el criterio si todos los modos incluidos producen y recuperan los datos sintéticos esperados, las incidencias se explican y los intentos fallidos conservan los puntos anteriores. Registra cada caso externo como aprobado, fallido o pendiente con el equipo y la acción probada.

Detén nuevas ejecuciones del modo afectado ante un hash inesperado, un punto incompleto mostrado como confirmado, una retirada sin revisión, un destino equivocado o datos ajenos modificados. Retira el horario del trabajo afectado, conserva el espacio y registra el estado. Para cualquier incidencia incluye hora local y zona, commit, modo, acción, mensaje y resultado de verificación. Comparte solo nombres relativos sintéticos y elimina rutas de usuario, credenciales y datos privados del informe. Una réplica dañada puede permitir recuperación y a la vez suspender la limpieza. Espacio insuficiente, origen ausente y archivos en uso requieren resolver el problema antes del reintento.

## Validaciones fuera de la aceptación de software

| Entorno | Comprobación pendiente y evidencia esperada |
|---|---|
| VM Windows con una cuenta de prueba | Programa una hora, cierra sesión y registra que el modo interactivo espera. Inicia sesión con el monitor habilitado y registra su aparición, hora, historial y estado. |
| VM con reinicio o suspensión controlados | Deja pasar una hora programada, vuelve a la sesión y registra si Windows recupera el horario, cuánto demora y si el punto se verifica. Ensaya reinicio abrupto de la VM por separado con una copia sintética pendiente. |
| Equipo de laboratorio con NTFS extraíble | Registra desconexión, retorno y disco sustituido. Comprueba que la identidad equivocada se rechaza, que el retorno permite verificar y que se conservan los incidentes. Registra geometría 512e o 4Kn y resultado de la lectura de prueba. |
| Nube de laboratorio | Usa archivos sintéticos locales, parcialmente descargados y solo en la nube. Comprueba el inventario, los avisos y que el ensayo no provoque hidratación involuntaria. |
| Otra cuenta o equipo de laboratorio | Prueba extracción de un punto sintético sin el índice local. Registra los límites de permisos y de reasociación de identidad. |
| Escritorio interactivo de pruebas | Comprueba teclado, foco, escala, pantallas y entrega visible de avisos. Mide respuesta de otras aplicaciones durante un respaldo en las configuraciones HDD/SSD y CPU disponibles. |

Cada validación externa conserva su propio resultado. Usa solo datos sintéticos. La aceptación de Windows en CI cubre arneses, terminaciones de proceso, tareas reales desde el paquete y reproducción de archivos distribuidos. El corte eléctrico físico queda excluido.

Consulta la matriz para ver la evidencia del commit y el alcance de cada comprobación. [Matriz de aceptación](ACEPTACION.md).
