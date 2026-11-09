# Rendimiento cotidiano

## Línea base

Medición en Windows 11 x64, compilación 26100. El arnés de cambios ejecuta el monitor real de `598fbc7` con registro desechable en C:, origen sintético en la SSD NVMe C: y destino sintético en el HDD SATA D:. Usa archivos pequeños de contenido repetido, un trabajo a la vez y la espera de dos segundos del monitor. Amplía la reconciliación periódica a una hora para separar las muestras. El tiempo de detección es la primera observación del estado pendiente por sondeo de 100 ms. El despacho y la confirmación se calculan desde la escritura con las marcas del intento durable. La restauración mide un archivo con el origen apartado. Son muestras locales de latencia, sujetas a caché, antivirus, escritura del diario y carga del equipo.

La simulación de historial ejecuta la ventana WPF Ruta, la consulta de bandeja y la reserva de un intento automático desde una compilación de `e98f233` con el arnés añadido. Crea un respaldo y una verificación reales por modo. Los registros de comparación e incidencia restantes son sintéticos. Se observa la mediana de cinco muestras calientes para Ruta y bandeja y de una reserva por modo. La prueba comienza con dos intentos por trabajo y después simula 21 días de actividad cada diez minutos en tres trabajos.

| Intentos por trabajo | Actualizar Ruta | Consultar bandeja | Reservar intento |
|---:|---:|---:|---:|
| 2 | 10,982 ms | 33,914 ms | 17,845 ms |
| 3.024 | 4.992,726 ms | 5.119,558 ms | 2.086,147 ms |

Las tres fechas de respaldo completo y las tres de verificación siguieron visibles en Ruta. La bandeja conservó la antigüedad consultable y los historiales conservaron las incidencias. La regresión del motor reprodujo que un alta aislada con un aviso válido confirmaba la copia por el recorrido completo. En C: → D:, el paquete de partida confirmó el alta de espejo en 30,175 s y el renombrado en 21,985 s. Ambos recorrieron el árbol completo. El primer llenado acumulativo del HDD seguía preparando archivos tras 17 minutos y se detuvo para concentrar la comparación en árboles ya poblados. Esa carga coincidió con las primeras muestras de espejo y puede haber elevado su latencia.

### Paquete `598fbc7`, 4.096 archivos entre C: y D:

Tiempos en segundos. «Pendiente» indica la primera observación de la señal. «Recuperar» corresponde a la versión afectada o, en bajas, a la anterior. Los tres modos también recuperaron el nombre anterior al renombrado.

| Modo | Cambio | Pendiente | Despacho | Ejecutor | Confirmación | Recuperar | Acotado |
|---|---|---:|---:|---:|---:|---:|---|
| Acumulativo | Edición | 1,012 | 3,430 | 19,894 | 23,324 | 0,105 | Sí |
| Acumulativo | Temporal | 0,784 | 3,246 | 12,398 | 15,645 | 0,072 | No |
| Acumulativo | Alta | 0,226 | 2,702 | 12,181 | 14,883 | 0,079 | No |
| Acumulativo | Renombrado | 0,338 | 2,731 | 10,654 | 13,385 | 0,073 | No |
| Acumulativo | Baja | 1,010 | 3,499 | 1,691 | 5,191 | 0,056 | No |
| Espejo | Edición | 0,240 | 2,662 | 20,114 | 22,776 | 0,106 | Sí |
| Espejo | Temporal | 0,565 | 3,009 | 24,609 | 27,618 | 0,082 | No |
| Espejo | Alta | 0,223 | 2,655 | 27,520 | 30,175 | 0,077 | No |
| Espejo | Renombrado | — | 0,618 | 21,367 | 21,985 | 0,066 | No |
| Espejo | Baja | 0,446 | 2,816 | 21,765 | 24,581 | 0,064 | No |
| Versionado | Edición | — | 0,518 | 3,574 | 4,092 | 0,115 | No |
| Versionado | Temporal | 1,015 | 3,456 | 3,114 | 6,570 | 0,103 | No |
| Versionado | Alta | 0,452 | 2,894 | 3,626 | 6,521 | 0,075 | No |
| Versionado | Renombrado | 0,900 | 3,364 | 2,940 | 6,304 | 0,071 | No |
| Versionado | Baja | 0,675 | 3,033 | 2,816 | 5,849 | 0,076 | No |

Las celdas sin observación del estado pendiente corresponden a dos despachos de menos de un segundo. La línea base comenzó el siguiente cambio tras leer el resultado anterior. El arnés posterior espera además que el monitor vuelva a indicar vigilancia. En acumulativo, la baja fue una comparación sin cambios y el archivo siguió recuperable en el punto vigente.

## Historial de varios trabajos

| Intentos por trabajo | Actualizar Ruta | Consultar bandeja | Reservar intento |
|---:|---:|---:|---:|
| 2 | 6,014 ms | 30,165 ms | 19,436 ms |
| 3.024 | 26,646 ms | 46,456 ms | 18,842 ms |

Ruta y bandeja comparten durante 30 segundos los intentos cerrados ya validados en memoria. Cada consulta vuelve a enumerar los intentos y lee los nuevos, los activos, los interrumpidos y los que admiten reintentos. **Actualizar** fuerza la lectura de disco. Una edición externa dentro de un intento cerrado puede esperar hasta la siguiente revalidación. La primera consulta y las revalidaciones vuelven a leer el historial. La bandeja comprueba por separado el catálogo recuperable y la vigencia de la última copia completa.

La reserva enumera los nombres de los intentos una vez, incluidos los que reservan secuencias ilegibles. Las decisiones que necesitan su contenido, como reanudar versionado o aplicar el presupuesto de retiradas del espejo, lo leen directamente de disco bajo el bloqueo de ejecución. El tiempo de reserva mide esa primera parte y excluye la lectura que realizan esas decisiones, el arranque del proceso y las operaciones del respaldo.
