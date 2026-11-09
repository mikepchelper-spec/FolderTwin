# Automatización continua en Windows

Esta matriz corresponde a la candidata en desarrollo a partir de la revisión Ruta. La evidencia de RC2 corresponde a los modos manual y programado.

## Contrato

Un proceso por usuario y registro permanece activo al cerrar la ventana y con la sesión iniciada, incluso bloqueada. El usuario puede activar el arranque al iniciar sesión. Después de una interrupción se compara el estado disponible antes de afirmar que el trabajo está al día. Windows debe estar encendido y despierto. Un estado transitorio del origen puede desaparecer antes de que el lector pueda abrirlo.

Los avisos del sistema permiten adelantar el próximo análisis. Una comparación al iniciar, tras errores o retorno de raíces y en intervalos periódicos cubre cambios sin aviso. El motor conserva la autoridad sobre identidad, integridad, publicación y recuperación. Los datos previos a reemplazos y retiradas ordinarias del espejo deben conservarse en un punto verificable antes de aplicar. Retención y purga siguen siendo manuales.

## Aceptación de software

| ID | Requisito | Prueba y estado |
|---|---|---|
| AUT-01 | Configuración explícita, pausa durable, preservación de trabajos existentes e importaciones desactivadas. | `--product-runs`, `--product-config` y `--product-automation-ui-smoke` aprobaron admisión, importación deshabilitada, consentimiento y pausa durable. Pendiente aceptación desde el paquete final. |
| AUT-02 | Monitor residente, arranque con Windows, exclusión, salud, parada y recuperación supervisada. | `--product-monitor-smoke` aprobó exclusión y pausa. El paquete `7084e1d` aprobó `--product-monitor-schedule-smoke` con una tarea real iniciada a demanda. Pendientes estado en Ruta, ciclo real de sesión y actualización de versión con monitor activo. |
| AUT-03 | Avisos agrupados con demora acotada y reconciliación por inicio, pérdida, interrupción y retorno. | `--product-watch` aprobó agrupación, ráfaga, aviso posterior, error inyectado y traslado real de un subárbol. Pendiente comprobar la convergencia tras caída y pérdida de avisos desde el paquete. |
| AUT-04 | Reintentos espaciados, equidad, concurrencia y control de intentos pendientes. | `--product-monitor-smoke` aprobó retorno del destino sin intervención, restauración sin origen en los tres modos y confirmó un segundo trabajo mientras el primero seguía ocupado. La recuperación supervisada se probó en AUT-02. Falta avanzar archivos estables dentro del mismo trabajo ocupado. |
| AUT-05 | Acumulativo, versionado sin puntos redundantes y espejo automático con retiradas recuperables. | `--product-runs` comprueba versionado sin punto redundante y detecta una edición con el mismo tamaño y fecha. `--product-auto-mirror` y la prueba de procesos aprobaron retirada recuperable y límites del espejo. Pendiente repetir desde el paquete final. |
| AUT-06 | Identidades, conflictos, anomalías, volúmenes, inventarios y espacio. | Pendientes regresiones específicas de automatización y VHDX. |
| AUT-07 | Rendimiento de reposo y ráfagas, hasta 329.000 archivos y múltiples trabajos. | Pendiente línea base y medición del candidato. |
| AUT-08 | Asistente y Ruta, ayudas, bandeja, incidencias y recuperación. | `--product-automation-ui-smoke` aprobó revisión, primera copia y retirada con ventana cerrada. Se añadieron controles globales, estado de vigilancia por trabajo, última comparación y apertura de incidencias desde bandeja. Pendiente aceptación de estos cambios desde el paquete y observación de escritorio sintético. |
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

## Política de espejo automático

El consentimiento opcional pertenece a la revisión local del trabajo y queda ligado a su modo e identidades. Pausar lo conserva. Desactivar la vigilancia lo retira. Exportar no transfiere esa autorización. Sin consentimiento se conserva la revisión manual para retiradas y cambios masivos.

Al aplicar con consentimiento, el motor prepara el inventario bajo el mismo bloqueo que usa para publicar el plan y comprueba el origen, el espejo, el registro y el catálogo anterior. Si una retirada o un reemplazo es admisible, registra sus rutas mediante hashes antes de publicar el plan durable. El diario conserva los datos anteriores y vuelve a comprobar las carpetas después de preparar los archivos y antes de mutar el espejo. Una caída después de registrar rutas las cuenta de forma conservadora aunque no se hayan aplicado. Reanudar un diario ya autorizado no vuelve a contarlas.

El límite de cada intento es un cuarto de las rutas previas, con un mínimo de 4 y un máximo de 256. El límite acumulado en una hora es dos quintos, con un mínimo de 8 y un máximo de 512 rutas distintas. Reescribir varias veces un documento no multiplica su coste acumulado. Una fecha posterior al reloj actual sigue contando si el reloj retrocede. El origen vacío, los catálogos dudosos y los conflictos externos requieren atención aunque el presupuesto alcance. La política limita el alcance de las retiradas y mantiene la purga definitiva en manos del usuario.

El asistente muestra el disparador por separado del modo y ofrece consentimiento aparte para el espejo. Guardar solicita la primera copia. La prueba WPF usa un registro desechable y un monitor real lanzado por el arnés. El ensayo nativo del Programador permanece separado. La prueba de los tres modos compara la restauración actual y la anterior con el origen apartado: acumulativo conserva el archivo retirado del origen, mientras que versionado y espejo lo conservan en el punto previo.

Ruta muestra la actividad observada de cada trabajo y conserva aparte las fechas de comparación, respaldo y verificación. El indicador de actividad vive en un registro acotado que solo cuenta mientras el monitor mantiene su bloqueo y renueva el estado. Los resultados de respaldo siguen en su propio historial durable. Aplicación permite quitar la tarea del monitor sin tocar los horarios diarios. La bandeja permite saltar al historial del primer trabajo que necesita atención.

## Presupuesto de rendimiento

Medir por separado el arranque del trabajador, su publicación y una verificación integral. En un SSD local con un solo trabajo disponible, el objetivo inicial es despachar en hasta 5 s tras un cambio aislado y en hasta 25 s durante una ráfaga continua. Estos límites excluyen espera por archivo ocupado, falta de espacio, reconexión y otro trabajo ya en curso. La confirmación de una edición de 12 bytes debe tomar hasta 15 s en un árbol de 4.096 archivos y hasta 60 s en uno de 329.000. El monitor inactivo debe promediar menos de 0,5 % de un núcleo y conservar menos de 100 MiB de memoria residente. Registrar también las lecturas, escrituras, memoria del trabajador y diferencias por modo y escala.

`--product-auto-scale <archivos> <modo>` mide la preparación, copia inicial, comparación sin cambios, edición de igual tamaño y fecha y recuperación de la muestra sin el origen. Usa datos sintéticos con 32 contenidos pequeños. La prueba mide el motor por separado del retraso de notificaciones y de la carga de varios trabajos. El presupuesto es un criterio de aceptación para el entorno medido.

En Windows 11, la primera medición de versionado con 4.096 archivos registró 8,08 s para el punto inicial, 7,08 s sin cambios y 8,99 s para confirmar una edición. Al reutilizar la cadena de directorios comprobada para archivos vecinos, las cifras fueron 3,94 s, 1,67 s y 1,68 s. El máximo observado de memoria del proceso fue 74,4 MB en la segunda corrida. Ambas restauraron la edición con el origen apartado. Son observaciones locales preliminares con carga concurrente distinta. La comprobación sigue leyendo y calculando el hash de cada archivo, con su propio handle que excluye escritores. El ensayo diferencial posterior aprobó 9 casos y 52 puntos en los tres modos.

Las primeras mediciones a 4.096 archivos confirmaron la edición en 59,29 s para acumulativo y 89,31 s para espejo. Después de unificar la revisión y publicación automáticas bajo el mismo bloqueo y reducir las consultas por archivo, acumulativo confirmó la edición en 8,41 s. Los recorridos de aprobación manual y reanudación siguen utilizando sus comprobaciones de diario. Se probó que una modificación posterior a la revisión bloquea la mutación del espejo. La consulta de fechas y atributos usa [FILE_BASIC_INFO](https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-file_basic_info) y conserva las comprobaciones de flujos, atributos extendidos y contenido. La auditoría de lectura aprobó sus 24 casos.

El monitor despachó ediciones aisladas de los tres modos en 2,99–3,45 s en una corrida de desarrollo. En una ventana de reposo de 15 s se observaron 60,3 MB de memoria, 0 bytes leídos y 87 bytes escritos por el proceso, con 0,94 % de un núcleo. La primera implementación observó 5,93 %. El objetivo de CPU todavía requiere evidencia con la última versión, que espera notificaciones en reposo y limita las lecturas de configuración. El ensayo con dos trabajos comprobó que un escritor abierto en uno no impide al otro confirmar y verificar su punto.

La comparación de 329.000 archivos creó su punto inicial en 119,18 s. Pendientes la comparación sin cambios y la edición de esa corrida, así como las mediciones finales de espejo, ráfagas, reposo y varios trabajos. En Windows, usar carpetas de compilación distintas si otra medición sigue ejecutando el binario anterior.
