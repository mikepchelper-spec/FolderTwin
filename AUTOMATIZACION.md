# Automatización continua en Windows

Esta matriz corresponde a la candidata en desarrollo a partir de la revisión Ruta. La evidencia de RC2 corresponde a los modos manual y programado.

## Contrato

Un proceso por usuario y registro permanece activo al cerrar la ventana y con la sesión iniciada, incluso bloqueada. El usuario puede activar el arranque al iniciar sesión. Después de una interrupción se compara el estado disponible antes de afirmar que el trabajo está al día. Windows debe estar encendido y despierto. Un estado transitorio del origen puede desaparecer antes de que el lector pueda abrirlo.

Los avisos del sistema permiten adelantar el próximo análisis. Una comparación al iniciar, tras errores o retorno de raíces y en intervalos periódicos cubre cambios sin aviso. El motor conserva la autoridad sobre identidad, integridad, publicación y recuperación. Los datos previos a reemplazos y retiradas ordinarias del espejo deben conservarse en un punto verificable antes de aplicar. Retención y purga siguen siendo manuales.

## Aceptación de software

| ID | Requisito | Prueba y estado |
|---|---|---|
| AUT-01 | Configuración explícita, pausa durable, preservación de trabajos existentes e importaciones desactivadas. | Admisión y pausa aprobadas localmente en `--product-runs`. Pendiente el recorrido completo desde la interfaz. |
| AUT-02 | Monitor residente, arranque con Windows, exclusión, salud, parada y recuperación supervisada. | Exclusión, trabajador separado y parada tras pausa aprobados en `--product-monitor-smoke`. Pendiente el supervisor nativo y su estado en Ruta. |
| AUT-03 | Avisos agrupados con demora acotada y reconciliación por inicio, pérdida, interrupción y retorno. | `--product-watch` aprobó agrupación, ráfaga, aviso posterior, error inyectado y traslado real de un subárbol. Pendiente comprobar la convergencia tras caída y pérdida de avisos desde el paquete. |
| AUT-04 | Reintentos espaciados, equidad, concurrencia y control de intentos pendientes. | `--product-monitor-smoke` aprobó retorno del destino sin intervención, copia con trabajador y restauración sin origen en versionado. Pendientes pruebas de equidad, ocupados y recuperación supervisada. |
| AUT-05 | Acumulativo, versionado sin puntos redundantes y espejo automático con retiradas recuperables. | Pendientes pruebas de contenido y restauración sin origen. |
| AUT-06 | Identidades, conflictos, anomalías, volúmenes, inventarios y espacio. | Pendientes regresiones específicas de automatización y VHDX. |
| AUT-07 | Rendimiento de reposo y ráfagas, hasta 329.000 archivos y múltiples trabajos. | Pendiente línea base y medición del candidato. |
| AUT-08 | Asistente y Ruta, ayudas, bandeja, incidencias y recuperación. | Pendientes pruebas WPF y observación de escritorio sintético. |
| AUT-09 | Paquete reproducible, actualización, retirada y acceso inequívoco. | Pendiente aceptación desde el paquete final. |

Los ensayos usan datos sintéticos y destinos desechables. Las comprobaciones de sesión, reinicio y suspensión reales, USB físico, otro equipo o cuenta, nube real, accesibilidad externa y observación del piloto mantienen su propio estado en [V1](V1.md) y [PILOTO](PILOTO.md).

## Primer hito de desarrollo

La revisión de ajustes añade vigilancia desactivada, activa o pausada. Los ajustes anteriores omiten el campo nuevo y conservan sus checksums. La autorización se vuelve a leer tras tomar el bloqueo de ejecución. Deshabilitar el trabajo retira esa autorización. Las exportaciones conservan el formato anterior y las importaciones requieren activación explícita.

El monitor toma un bloqueo por registro, instala avisos antes de su primera comparación y despacha un trabajador a la vez. Usa una señal acotada por trabajo, dos segundos de calma, veinte segundos de espera máxima, sondeo de un segundo y reconciliación cada quince minutos. Son parámetros iniciales pendientes de medición. Los errores de vigilancia solicitan una comparación y reconstruyen la suscripción. Los reintentos de disponibilidad se espacian de cinco a ochenta segundos. Tras reiniciar el proceso se compara el origen disponible, aunque no queden avisos en memoria.

La prueba local `--product-monitor-smoke` ejecutó el binario de desarrollo de Windows con un registro desechable. Comprobó arranque, exclusión, edición del origen, desaparición y retorno de la misma carpeta de destino, pausa y restauración del último contenido sin origen. CI queda configurada para ejecutar este recorrido desde el paquete. Las comparaciones sin cambios, la política de retiradas del espejo, los estados de salud y el supervisor del Programador de tareas siguen abiertos.
