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
| AUT-03 | Avisos agrupados con demora acotada y reconciliación por inicio, pérdida, interrupción y retorno. | `--product-watch` aprobó agrupación, aviso posterior y traslado real de un subárbol. El paquete `09ee92e` recuperó los bytes tras suprimir avisos y tras inyectar desbordamiento con `--product-monitor-reconciliation-smoke`. |
| AUT-04 | Reintentos espaciados, equidad, concurrencia y control de intentos pendientes. | `--product-monitor-smoke` aprobó retorno del destino, restauración sin origen y un segundo trabajo mientras el primero seguía ocupado. `--product-auto-partial` verificó un punto recuperable de archivos estables en los tres modos. `--product-runs` terminó un trabajador automático real por modo, recuperó la misma generación y rechazó el horario y el intento manual mientras el trabajador poseía el bloqueo. |
| AUT-05 | Acumulativo, versionado sin puntos redundantes y espejo automático con retiradas recuperables. | `--product-runs` comprueba versionado sin punto redundante y detecta una edición con el mismo tamaño y fecha. `--product-auto-mirror` y la prueba de procesos aprobaron retirada recuperable y límites del espejo. Pendiente repetir desde el paquete final. |
| AUT-06 | Identidades, conflictos, anomalías, volúmenes, inventarios y espacio. | En Windows, `--product-monitor-cross-volume-smoke` aprobó primera copia, edición, ausencia y retorno de la carpeta, pausa y restauración con origen en C: y destino desechable en D: en los tres modos. También rechazó una carpeta sustituta con el marcador copiado. El arnés VHDX cubre la ruta automática en código. Su ejecución con volumen lleno y cambio de letra requiere un runner aislado con permisos de montaje. |
| AUT-07 | Rendimiento de reposo y ráfagas, hasta 329.000 archivos y múltiples trabajos. | El monitor cumplió los presupuestos medidos de reposo y despacho. Versionado cumplió el presupuesto a 329.000 archivos. El cuadro de mediciones identifica las comprobaciones restantes. |
| AUT-08 | Asistente y Ruta, ayudas, bandeja, incidencias y recuperación. | `--product-automation-ui-smoke` aprobó revisión, primera copia y retirada con ventana cerrada en el paquete `c70ac2c`. Pendientes aceptación en el paquete actual y observación de escritorio sintético. |
| AUT-09 | Paquete reproducible, actualización, retirada y acceso inequívoco. | Pendiente aceptación desde el paquete final. |

Los ensayos usan datos sintéticos y destinos desechables. Las comprobaciones de sesión, reinicio y suspensión reales, USB físico, otro equipo o cuenta, nube real, accesibilidad externa y observación del piloto mantienen su propio estado en [V1](V1.md) y [PILOTO](PILOTO.md).

## Primer hito de desarrollo

La revisión de ajustes añade vigilancia desactivada, activa o pausada. Los ajustes anteriores omiten el campo nuevo y conservan sus checksums. La autorización se vuelve a leer tras tomar el bloqueo de ejecución. Deshabilitar el trabajo retira esa autorización. Las exportaciones conservan el formato anterior y las importaciones requieren activación explícita.

El monitor toma un bloqueo por registro, instala avisos antes de su primera comparación y despacha un trabajador a la vez. Usa una señal acotada por trabajo. Dos segundos de calma agrupan los avisos de un guardado y veinte segundos de espera máxima permiten despachar aunque los cambios sigan. Reconciliar cada quince minutos limita el tiempo sin una nueva comparación cuando se pierden avisos y espacia los recorridos completos. El sondeo durante actividad es de un segundo. Los errores de vigilancia solicitan una comparación y reconstruyen la suscripción. Los reintentos de disponibilidad se espacian de cinco a ochenta segundos. Tras reiniciar el proceso se compara el origen disponible aunque no queden avisos en memoria.

Para ajustar intervalos en un ensayo, detén la vigilancia, crea `monitor-timing.json` en `%LocalAppData%\FolderTwin\jobs` y vuelve a iniciar el monitor. El archivo acepta los campos `QuietSeconds` (1–10), `MaximumWaitSeconds` (5–60) y `ReconcileSeconds` (10–3600), con los valores en orden ascendente. El ejemplo reproduce los valores iniciales. Retira el archivo para volver a ellos. Intervalos cortos aumentan las comparaciones y pueden elevar el consumo.

```json
{"QuietSeconds":2,"MaximumWaitSeconds":20,"ReconcileSeconds":900}
```

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

La medición de espejo con 4.096 archivos confirmó la edición en 6,02 s y restauró los bytes esperados. Para árboles desde 8.192 archivos, la revisión automática puede leer en paralelo con hasta cuatro lectores y grupos de hasta 64 archivos del mismo directorio. Cada archivo mantiene su propia apertura y validación. Las réplicas reescritas se comprueban con la huella del manifiesto canónico validado. El lector del catálogo sigue aceptando los formatos anteriores. Las regresiones aprobaron 24 casos de lectura y 11 de catálogo.

| Versionado · 329.000 archivos | Punto inicial | Sin cambios | Confirmar edición | Restaurar archivo con el origen apartado |
|---|---:|---:|---:|---:|
| `f758340` | 119,18 s | 109,48 s | 109,60 s | 3,69 s |
| `c70ac2c` | 72,37 s | 51,21 s | 68,68 s | 4,25 s |
| Lectura paralela y catálogo por bloques | 48,67 s | 31,43 s | 40,95 s | 4,46 s |

La última corrida de 329.000 archivos leyó 444 MB, escribió 220 MB y tuvo un pico de 480 MB residentes contando la restauración. Con 16.384 archivos, la misma revisión confirmó una edición en 3,00 s en versionado, 10,66 s en acumulativo y 9,15 s en espejo. Los primeros llenados de 16.384 archivos en acumulativo y espejo tomaron 374,87 s y 300,72 s. Queda por medir el coste a mayor escala de los modos que publican una carpeta actual.

Desde el paquete `c70ac2c`, las ediciones aisladas despacharon en 2,77–2,82 s en los tres modos y el monitor promedió 0,21 % de un núcleo en 15 s de reposo. Un ensayo posterior de desarrollo midió 120,02 s de reposo, 0,27 % de un núcleo, 62,20 MB residentes, 8.272 bytes leídos y 696 escritos. En ese ensayo una ráfaga de 64 guardados mediante archivo temporal y reemplazo despachó en 21,94 s. Se restauró el contenido exacto del último guardado y el mismo ensayo comprobó reconexión, equidad y sustitución. El ensayo inyectado de reconciliación usa una suscripción real con sus callbacks desactivados. Comprueba el contenido de los dos puntos con el origen apartado, uno sin avisos y el otro tras inyectar la condición que usa el callback de error.

## Puntos parciales recuperables

Si la apertura de un archivo del origen falla por un escritor activo durante el análisis automático, se comprueban los demás archivos con la misma política de lectura. Un inventario con rutas inesperadas, enlaces o errores distintos de un archivo ocupado sigue sujeto a revisión. Los archivos legibles se verifican y se guardan como objetos antes de publicar un punto parcial. El punto queda registrado con un identificador que indica su alcance, conserva las dos réplicas y se puede verificar o recuperar con el origen ausente. Una interrupción después de escribir el catálogo pendiente se reanuda desde los objetos ya preparados. Un borrador ilegible requiere atención.

Ruta muestra el intento como pendiente, conserva las fechas de respaldo completo y verificación integral y permite recuperar el punto parcial. No utiliza puntos parciales para deducir retiradas ni sustituir un punto completo. La retención selecciona por separado puntos parciales y completos. Una nueva comparación con los mismos archivos legibles no crea otro punto. La prueba interrumpió el guardado del catálogo, apartó el origen, recuperó el punto y después confirmó un respaldo completo en los tres modos al devolver la carpeta y cerrar el escritor.

Las compilaciones anteriores a esta función no reconocen los identificadores de puntos parciales. Usa una compilación que los admita para abrir ese historial. Los puntos completos anteriores conservan sus identificadores.
