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

### Paquete `335a98a`, 4.096 archivos entre C: y D:

| Modo | Cambio | Pendiente | Despacho | Ejecutor | Confirmación | Recuperar | Acotado |
|---|---|---:|---:|---:|---:|---:|---:|---|
| Acumulativo | Edición | 0,110 | 2,417 | 7,950 | 10,366 | 0,095 | Sí |
| Acumulativo | Temporal | 0,009 | 2,419 | 7,315 | 9,734 | 0,072 | Sí |
| Acumulativo | Alta | 0,008 | 2,411 | 7,781 | 10,192 | 0,078 | Sí |
| Acumulativo | Renombrado | 0,012 | 2,417 | 6,297 | 8,714 | 0,075 | Sí |
| Acumulativo | Baja | 0,009 | 2,422 | 0,264 | 2,686 | 0,064 | Sí |
| Espejo | Edición | 0,113 | 2,422 | 7,899 | 10,321 | 0,095 | Sí |
| Espejo | Temporal | 0,009 | 2,415 | 7,813 | 10,228 | 0,076 | Sí |
| Espejo | Alta | 0,009 | 2,416 | 7,309 | 9,725 | 0,073 | Sí |
| Espejo | Renombrado | 0,009 | 2,415 | 7,024 | 9,440 | 0,075 | Sí |
| Espejo | Baja | 0,007 | 2,416 | 5,927 | 8,343 | 0,067 | Sí |
| Versionado | Edición | 0,015 | 2,414 | 3,207 | 5,620 | 0,108 | No |
| Versionado | Temporal | 0,010 | 2,409 | 3,356 | 5,765 | 0,090 | No |
| Versionado | Alta | 0,007 | 2,431 | 3,237 | 5,667 | 0,067 | No |
| Versionado | Renombrado | 0,013 | 2,412 | 2,516 | 4,928 | 0,068 | No |
| Versionado | Baja | 0,009 | 2,414 | 2,740 | 5,154 | 0,068 | No |

El arnés posterior separa las comparaciones sin cambios iniciadas antes de cada escritura del intento que realmente confirma la operación. Las conserva en el registro y en la salida del ensayo. Acumulativo conservó las rutas exclusivas del destino. Espejo retiró del árbol actual las rutas antiguas y restauró sus bytes desde el punto anterior. Los tres modos recuperaron el archivo editado, el guardado temporal, el alta y las rutas anteriores al renombrado y a la baja con el origen apartado. La preparación verificada de 4.096 archivos tomó 12,260 s en acumulativo y 37,727 s en espejo. El primer respaldo de versionado tomó 28,612 s de ejecutor.

Se repitió `598fbc7` en espejo con la atribución actualizada y sin el primer llenado concurrente. Es la comparación preferida para ese modo. El ejecutor antiguo todavía usó la comparación completa para alta, renombrado, baja y guardado por temporal. La edición ordinaria ya usaba el recorrido acotado y en esa pareja de muestras fue más rápida con la versión anterior.

| Cambio en espejo | Confirmación `598fbc7` | Confirmación `335a98a` | Ejecutor anterior → nuevo |
|---|---:|---:|---:|
| Edición | 4,293 s | 10,321 s | 1,837 → 7,899 s |
| Temporal | 12,283 s | 10,228 s | 9,826 → 7,813 s |
| Alta | 12,983 s | 9,725 s | 10,522 → 7,309 s |
| Renombrado | 11,703 s | 9,440 s | 9,245 → 7,024 s |
| Baja | 11,172 s | 8,343 s | 8,704 → 5,927 s |

El tamaño de muestra y la variación del HDD impiden atribuir toda diferencia al código. La evidencia directa del cambio es que la confirmación ordinaria de esas altas y renombrados usó el recorrido acotado. Los catálogos duplicados se siguen publicando completos y se conserva su coste de lectura y escritura.

## Historial de varios trabajos

| Intentos por trabajo | Actualizar Ruta | Consultar bandeja | Reservar intento |
|---:|---:|---:|---:|
| 2 | 6,014 ms | 30,165 ms | 19,436 ms |
| 3.024 | 26,646 ms | 46,456 ms | 18,842 ms |

La extracción de `335a98a` repitió las comprobaciones con 3.024 intentos por trabajo y registró medianas de 21,492 ms en Ruta, 41,848 ms en bandeja y 20,088 ms al reservar. Conservó la fecha del respaldo completo, la verificación integral y la incidencia más reciente en los tres trabajos.

Ruta y bandeja comparten durante 30 segundos los intentos cerrados ya validados en memoria. Cada consulta vuelve a enumerar los intentos y lee los nuevos, los activos, los interrumpidos y los que admiten reintentos. **Actualizar** fuerza la lectura de disco. Una edición externa dentro de un intento cerrado puede esperar hasta la siguiente revalidación. La primera consulta y las revalidaciones vuelven a leer el historial. La bandeja comprueba por separado el catálogo recuperable y la vigencia de la última copia completa.

La reserva enumera los nombres de los intentos una vez, incluidos los que reservan secuencias ilegibles. Las decisiones que necesitan su contenido, como reanudar versionado o aplicar el presupuesto de retiradas del espejo, lo leen directamente de disco bajo el bloqueo de ejecución. El tiempo de reserva mide esa primera parte y excluye la lectura que realizan esas decisiones, el arranque del proceso y las operaciones del respaldo.

## Regresiones y distribución local

Se hicieron dos compilaciones Windows x64 a partir de extracciones independientes del commit `335a98a01061768e14862d6f25f064d9b643a49c`. Incluyen el runtime .NET 10.0.14. Coincidieron los 500 hashes de archivos y los ZIP creados con orden ordinal y fecha de entrada fija. `Test-Portable.ps1` validó ambas carpetas y la extracción final. La comprobación enlaza el arnés actualizado a `CopyProbe.dll` de la extracción y lanza su `FolderTwin.exe`.

| Artefacto | SHA-256 |
|---|---|
| `FolderTwin-win-x64-335a98a.zip` | `5bbbce75f02d83ccfcd061ef3f989be9c32675a3b5170e320133b00425326995` |
| `SHA256SUMS.txt` | `cb0aa14fd61a1fa4bd04e4f6012f20eef4de11b793ba1577730b1255bcbb850d` |

Desde esa extracción aprobaron el arnés de espejo y consentimiento, las señales de cambios, los puntos parciales y disco lleno inyectado, los intentos persistentes, 24 casos de lectores y los trabajadores de restauración. Se comprobó la reanudación tras retirar o promover archivos, la vuelta al recorrido completo al reemplazar una carpeta, el rechazo de una retirada masiva y la conservación de conflictos externos. Los arneses WPF aprobaron historial largo, bandeja, recuperación de avisos perdidos y desbordamiento inyectado, asistente de Ruta, respaldo con la interfaz cerrada, pausa y recorridos de foco y temas. El ensayo de compatibilidad generó los tres modos con `598fbc7` y comprobó con `335a98a` registro, exportación, historial, verificación y restauración independiente del árbol y sus bytes con el origen apartado.

El VHDX lleno y cambio de letra sigue pendiente para `598fbc7`. El piloto personal sigue aplazado. Los ensayos usan datos sintéticos y el informe acompaña al paquete como evidencia posterior.
