# Automatización continua en Windows

Esta matriz corresponde a la candidata en desarrollo a partir de la revisión Ruta. La evidencia de RC2 corresponde a los modos manual y programado.

## Contrato

Un proceso por usuario y registro permanece activo al cerrar la ventana y con la sesión iniciada, incluso bloqueada. El usuario puede activar el arranque al iniciar sesión. Después de una interrupción se compara el estado disponible antes de afirmar que el trabajo está al día. Windows debe estar encendido y despierto. Un estado transitorio del origen puede desaparecer antes de que el lector pueda abrirlo.

Los avisos del sistema permiten adelantar el próximo análisis. Una comparación al iniciar, tras errores o retorno de raíces y en intervalos periódicos cubre cambios sin aviso. El motor conserva la autoridad sobre identidad, integridad, publicación y recuperación. Los datos previos a reemplazos y retiradas ordinarias del espejo deben conservarse en un punto verificable antes de aplicar. Retención y purga siguen siendo manuales.

## Aceptación de software

| ID | Requisito | Prueba y estado |
|---|---|---|
| AUT-01 | Configuración explícita, pausa durable, preservación de trabajos existentes e importaciones desactivadas. | Admisión y pausa aprobadas localmente en `--product-runs`. Pendiente el recorrido completo desde la interfaz. |
| AUT-02 | Monitor residente, arranque con Windows, exclusión, salud, parada y recuperación supervisada. | `--product-monitor-smoke` aprobó exclusión y pausa. El paquete `7084e1d` aprobó `--product-monitor-schedule-smoke` con una tarea real iniciada a demanda. Pendientes estado en Ruta, ciclo real de sesión y actualización de versión con monitor activo. |
| AUT-03 | Avisos agrupados con demora acotada y reconciliación por inicio, pérdida, interrupción y retorno. | `--product-watch` aprobó agrupación, ráfaga, aviso posterior, error inyectado y traslado real de un subárbol. Pendiente comprobar la convergencia tras caída y pérdida de avisos desde el paquete. |
| AUT-04 | Reintentos espaciados, equidad, concurrencia y control de intentos pendientes. | `--product-monitor-smoke` aprobó retorno del destino sin intervención, copia con trabajador y restauración sin origen en versionado. Pendientes pruebas de equidad, ocupados y recuperación supervisada. |
| AUT-05 | Acumulativo, versionado sin puntos redundantes y espejo automático con retiradas recuperables. | `--product-runs` comprueba versionado sin punto redundante y detecta una edición con el mismo tamaño y fecha. Pendiente la política de espejo y su recuperación desde paquete. |
| AUT-06 | Identidades, conflictos, anomalías, volúmenes, inventarios y espacio. | Pendientes regresiones específicas de automatización y VHDX. |
| AUT-07 | Rendimiento de reposo y ráfagas, hasta 329.000 archivos y múltiples trabajos. | Pendiente línea base y medición del candidato. |
| AUT-08 | Asistente y Ruta, ayudas, bandeja, incidencias y recuperación. | Pendientes pruebas WPF y observación de escritorio sintético. |
| AUT-09 | Paquete reproducible, actualización, retirada y acceso inequívoco. | Pendiente aceptación desde el paquete final. |

Los ensayos usan datos sintéticos y destinos desechables. Las comprobaciones de sesión, reinicio y suspensión reales, USB físico, otro equipo o cuenta, nube real, accesibilidad externa y observación del piloto mantienen su propio estado en [V1](V1.md) y [PILOTO](PILOTO.md).

## Primer hito de desarrollo

La revisión de ajustes añade vigilancia desactivada, activa o pausada. Los ajustes anteriores omiten el campo nuevo y conservan sus checksums. La autorización se vuelve a leer tras tomar el bloqueo de ejecución. Deshabilitar el trabajo retira esa autorización. Las exportaciones conservan el formato anterior y las importaciones requieren activación explícita.

El monitor toma un bloqueo por registro, instala avisos antes de su primera comparación y despacha un trabajador a la vez. Usa una señal acotada por trabajo, dos segundos de calma, veinte segundos de espera máxima, sondeo de un segundo y reconciliación cada quince minutos. Son parámetros iniciales pendientes de medición. Los errores de vigilancia solicitan una comparación y reconstruyen la suscripción. Los reintentos de disponibilidad se espacian de cinco a ochenta segundos. Tras reiniciar el proceso se compara el origen disponible, aunque no queden avisos en memoria.

La prueba local `--product-monitor-smoke` ejecutó el binario de desarrollo de Windows con un registro desechable. Comprobó arranque, exclusión, edición del origen, desaparición y retorno de la misma carpeta de destino, pausa y restauración del último contenido sin origen. CI queda configurada para ejecutar este recorrido desde el paquete.

Las comparaciones automáticas sin cambios conservan la fecha del respaldo anterior. En versionado, el motor compara un inventario con hashes de contenido contra las dos réplicas del último catálogo retenido, elegido por secuencia del registro. En acumulativo y espejo, utiliza la revisión de conflictos, contenido y metadatos. El resultado `Unchanged` no verifica por completo los objetos ni publica una generación. Se mantiene la verificación explícita. El coste de recorrer los árboles sigue pendiente de medición.

## Supervisión nativa

El Programador de tareas conserva una definición por usuario e identidad del registro. La tarea acepta inicio bajo demanda y opcionalmente al iniciar sesión. El supervisor valida la tarea y su ejecutable instalado, conserva un monitor hijo y vuelve a iniciarlo tras una caída con esperas de 5, 10 y 20 segundos. Una salida voluntaria termina el supervisor. Quitar o sustituir la tarea impide nuevos despachos y deja que el trabajador ya iniciado termine por separado. El monitor actualiza un indicador local de actividad mientras posee el bloqueo del registro. Este indicador describe al proceso y no el estado del respaldo.

El 8 de noviembre de 2026 se verificaron los 500 archivos del paquete `7084e1d` y se ejecutó su prueba nativa con un registro desechable. La prueba inspeccionó ambos ajustes de arranque, inició la tarea a demanda, terminó el proceso del monitor identificado por su bloqueo, escribió un cambio mientras estaba caído, observó otro PID, restauró el contenido nuevo con el origen apartado y retiró la tarea. El primer diseño dependía del reintento nativo tras la terminación del monitor. En la prueba a demanda, Windows conservó el código de terminación pero no reinició el proceso durante los dos minutos observados. El supervisor hijo resolvió ese caso. El ensayo se ejecutó desde una tarea temporal porque el Programador no veía la instalación estable creada desde la sesión de herramientas. No se cambió de usuario ni se cerró sesión.
