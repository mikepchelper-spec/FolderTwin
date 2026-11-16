# Crecimiento y capacidad del respaldo automático

## Purga de metadatos en desarrollo

La corrección de A14-06 añade fin de vida a los catálogos, descriptores y listas auxiliares de las fechas definitivamente purgadas. La cuarentena conserva esos archivos para deshacer. Tras la gracia y la autorización de purga, dos comprobantes y el registro replicado fijan qué metadatos se retiraron. El estado vivo deja de conservar esas generaciones. Los comprobantes, diarios de retención y objetos purgados y pequeños estados de espejo siguen ocupando espacio. Una purga anterior puede completar explícitamente esta limpieza; abrir el respaldo conserva su estado.

La medición de desarrollo usa .NET 10.0.14, Windows 26100 y un espacio sintético en C:. `--retention-scale` crea ocho fechas de 4.096 archivos con contenidos compartidos, aparta el origen, conserva tres fechas y purga cinco. Mantiene los límites y contadores del arnés existente. Los datos siguientes provienen de una ejecución nueva; no sustituyen las mediciones históricas entre C: y D: de las secciones posteriores. [Salida de la medición](experiments/CopyProbe/metadata-retention-evidence.txt).

| Medida | Resultado |
| --- | ---: |
| Tamaño lógico antes de cuarentena | 21.842.903 B |
| Tamaño lógico después de purgar, incluidos comprobantes | 8.221.658 B |
| Espacio lógico neto liberado | 13.621.245 B |
| Catálogos y descriptores candidatos | 13.644.005 B |
| Revisión / cuarentena / purga | 0,265 / 0,767 / 0,690 s |
| Máximo residente observado del proceso | 60.809.216 B |

`--storage-safety` repitió el recorrido de disco lleno y comprobó la purga de dos fechas por modo, restauración con el origen apartado y siguiente copia automática. Los bytes de metadatos candidatos fueron 5.705 en acumulativo, 4.719 en versionado y 5.561 en espejo. Sus hashes de restauración coinciden con los de la tabla histórica de generaciones reanudadas. En el caso separado de tres fechas con contenidos de 128 KiB, la segunda cuarentena ocupó 540.221 B lógicos y 573.304 B asignados; tras purgar quedaron 411.103 / 437.776 B. Se liberaron 129.118 B lógicos y 135.528 B asignados netos. La asignación excluye MFT, índices y metadatos de volumen. Los nuevos comprobantes pueden costar más que los catálogos de un inventario diminuto; la revisión muestra candidatos, sin prometer ahorro neto exacto.

Las diez transiciones de purga, ocho casos de eventos replicados parciales, pérdida/corrupción y cinco mutaciones semánticas de comprobantes, reanudación sin origen, cambio de reloj y terminación real de trabajador pasaron `--retention`. El registro compactado conservó la prueba de purga cerca de 16 MiB. `--product-retention-smoke` verificó revisión, desglose, cuarentena/deshacer, gracia y purga desde WPF. Esta medición aún corresponde a desarrollo, no al ZIP final. USB, caída física y 329.000 archivos con el nuevo protocolo no se acreditan con este ensayo.

## Línea base de `335a98a`

Se verificó el paquete de Windows x64 y sus 500 archivos antes de probar. Windows 11, compilación 26100, con origen sintético en SSD NVMe C: y destinos dedicados en HDD SATA D:. El monitor del paquete vigiló los tres modos en un registro nuevo, con la interfaz cerrada y un ejecutor a la vez. Los árboles de acumulativo y espejo se prepararon y verificaron antes de iniciar la vigilancia. Versionado hizo su primera copia en el monitor. Los 4.096 archivos de cada trabajo usan 32 contenidos pequeños repetidos. Las fases se dispararon tras observar vigilancia y se comprimieron a minutos. La comparación periódica fue de 60 s y la espera ordinaria de cambios se mantuvo en 2 s. La ráfaga usó ocho guardados por trabajo, separados por 180 ms.

El arnés `--storage-growth` mide desde la edición hasta el inicio y resultado registrados por cada ejecutor. Entre fases inventaría el destino y los intentos de cada trabajo en C:. El tamaño lógico y la asignación por archivo provienen de `FILE_STANDARD_INFO`. La suma de asignación excluye índices de directorio, MFT, metadatos de volumen y tráfico de escritura que no queda en archivos. Las 56 observaciones incluyen puntos, intentos, latencias y los recuentos y bytes de cada categoría. [Datos por fase](experiments/AutomationAcceptance/storage-growth-evidence.csv) · [Asignación de archivos en Windows](https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-file_standard_info).

| Modo | Puntos iniciales → finales | Árbol actual final | Objetos finales | Catálogo + réplica final | Diarios finales | Historial final en C: |
|---|---:|---:|---:|---:|---:|---:|
| Acumulativo | 1 → 6 | 49.191 B | 447 B | 10.964.202 B | 10.994.286 B | 10.433 B |
| Versionado | 1 → 7 | 0 B | 447 B | 12.789.212 B | 6.409.338 B | 11.935 B |
| Espejo protegido | 1 → 7 | 49.167 B | 447 B | 12.788.978 B | 11.909.148 B | 12.146 B |

Los tamaños de esta tabla son lógicos. La cuarentena estaba vacía. El reposo añadió intentos sin cambios y conservó un punto por trabajo. Acumulativo conservó el archivo borrado y no añadió un punto en esa fase. El primer cambio de 12 bytes añadió aproximadamente 0,91 MB por réplica en los tres modos, unos 0,91 MB de descriptor en versionado y unos 1,83 MB de diarios en acumulativo y espejo. El último guardado de la ráfaga se recuperó con el origen apartado en los tres modos. Su SHA-256 fue `6362f0cc70fc59adf788c84fbe199735bf46a263bfd966be9468ecf2f96670a9`.

En la ráfaga, las confirmaciones desde la primera escritura tardaron 12,072 s en acumulativo, 21,325 s en espejo y 26,476 s en versionado. Los despachos ocurrieron a 4,444 s, 13,533 s y 22,676 s, respectivamente. Los tiempos incluyen la espera del monitor por el trabajo anterior.

La prueba de 329.000 archivos del mismo ZIP usa versionado para medir el coste de los catálogos en una carpeta grande. Su primer punto ocupó 146.695.392 bytes lógicos y 146.702.336 asignados entre las dos réplicas. Los diarios sumaron 73.353.996 bytes lógicos y 73.364.152 asignados. Los objetos de datos solo sumaron 384 bytes lógicos porque el conjunto repite 32 contenidos. Con siete puntos, los catálogos sumaron 1.026.868.548 bytes lógicos, los diarios 513.449.006 y los objetos 447. El destino totalizó 1.540.318.001 bytes lógicos y 1.540.391.688 asignados. El historial de C: sumó 13.042 bytes lógicos. El reposo conservó una generación, las seis fases con cambios añadieron una por fase y el último guardado se restauró con el SHA-256 de la ráfaga anterior. La comparación periódica se fijó a 180 s para esta escala. Las confirmaciones de cambios tardaron entre 35,101 y 46,092 s. Los tamaños de otros modos y cargas dependen de sus inventarios y datos.

## Capacidad y retención en la línea base

El arnés `--storage-safety` llamó la comprobación de reserva al iniciar el análisis con disponibilidad sintética cero. Los tres modos rechazaron el intento antes de publicar. El resultado durable usó el código genérico `0x80131620` y habló de una reserva del laboratorio. Ruta solo reconocía como falta de espacio el error nativo, por lo que ambos fallos se mostraban de forma distinta. Al inyectar `0x80070070` durante la publicación se conservó el punto previo, otro trabajo pudo confirmar y el intento posterior reanudó la misma generación. Los límites inyectados se aplicaron a las operaciones del arnés y la capacidad de los discos permaneció disponible.

En un almacén de versionado con tres puntos, cada fecha tenía un objeto exclusivo de 128 KiB y todas compartían otro. La cuarentena trasladó el objeto expirable dentro del mismo volumen y añadió 784 bytes de asignación de archivos al total. La primera cuarentena rechazó la purga antes de la gracia. Tras deshacer, el punto antiguo volvió a restaurarse con el origen apartado. Se revisó una segunda cuarentena y se purgó con el reloj de prueba avanzado. La purga liberó 131.008 bytes netos de asignación de archivos. Los dos puntos retenidos pasaron verificación y restauración con el origen apartado.

## Correcciones de `6f782af`

El rechazo previo por reserva insuficiente usa el mismo código durable de disco lleno que el fallo de publicación. La interfaz conserva la fecha del último respaldo completo, muestra la acción en Ruta e historial y abre primero el trabajo con falta de espacio desde el aviso de bandeja. Versionado comprueba antes de copiar que cabe el tamaño lógico mínimo del descriptor y las dos réplicas, además de la reserva. La comprobación inicial de espejo automático contempla los cuatro documentos que coexisten antes de confirmar la actualización. Los objetos y la asignación real pueden elevar el espacio necesario. El manejo del fallo de escritura sigue siendo necesario si el espacio cambia durante una copia.

Cuando acumulativo o espejo termina de aplicar una actualización automática, puede soltar una lista temporal de preparación si su huella coincide con la del punto y el descriptor durable confirma esa misma huella e identidad. Conserva el plan y los estados del diario. Una actualización pendiente, un inventario distinto o un documento que no coincide con su huella conserva su preparación. Se mantienen las dos réplicas y el proceso manual de retención. La corrección reduce el espacio que queda ocupado después de la publicación. La preparación sigue escribiéndose antes de aplicar para poder reanudar una interrupción.

La repetición con los tres trabajos de 4.096 archivos conservó los mismos recuentos de puntos por fase. Volvió a restaurar el último guardado con el mismo SHA-256 en los tres modos y con el origen apartado.

| Modo | Puntos finales | Diario lógico anterior → nuevo | Diario asignado anterior → nuevo | Confirmación de ráfaga anterior → nueva |
|---|---:|---:|---:|---:|
| Acumulativo | 6 | 10.994.286 → 5.510.988 B | 11.058.056 → 5.540.744 B | 12,072 → 6,340 s |
| Versionado | 7 | 6.409.338 → 6.410.033 B | 6.439.096 → 6.439.096 B | 26,476 → 12,004 s |
| Espejo protegido | 7 | 11.909.148 → 6.427.875 B | 11.968.072 → 6.446.664 B | 21,325 → 9,385 s |

Acumulativo y espejo terminaron cada uno con unos 5,5 MB menos de diario asignado. Los catálogos y sus réplicas conservan su estructura y su coste por punto. Las latencias variaron también en versionado y el alcance de algunos intentos cambió entre muestras. Son tiempos observados bajo una carga de prueba, sin atribuir la diferencia de latencia a la reducción de espacio.

## Fallos de espacio y retención desde la extracción nueva

El ensayo inyectó solo la reserva de 1 MiB como disponibilidad en la comprobación que usa cada modo. Los tres rechazaron antes de copiar y guardaron `0x80070070`. La segunda ejecución inyectó el mismo código durante publicación, dejó el punto anterior consultable y permitió que otro trabajo confirmara y verificara su propia copia. Tras retirar el límite se reanudó la generación interrumpida y permaneció el intento fallido en el historial. Los hashes esperados se calcularon de manera independiente sobre la carga sintética.

| Modo | SHA-256 del punto anterior tras ambos fallos | SHA-256 de la generación reanudada |
|---|---|---|
| Acumulativo | `5c10503f19e8888fcbae4b55fd9040a7aba0275fa5c47c47c46cb488b97866f3` | `8dfb74ed1279c741a908cda60cb66e5d3ef083b0dccb423d95070a52060f942f` |
| Versionado | `1e51c5c41957e7ac75aeb7c5a44c73dea954534451d7bf2048bf4b10ccd12090` | `8f458bd4729ec634c538f9b45075921aa92357295755487a30de314ceef3ca50` |
| Espejo protegido | `7c7ae47d29c4d54a4d8da7b4bbf0df6c5533cb661b7405e63ec822575972d667` | `9d841c7263eaae80b7ab0ea53ab486192801c88d0d6d778b97eb026064600ea3` |

Todas estas restauraciones se hicieron con el origen apartado. El arnés WPF confirmó el aviso por trabajo, la acción de reintento, el historial, la fecha del respaldo completo y la selección prioritaria del trabajo en bandeja. El arnés de espejo confirmó que se conserva la preparación durante una interrupción, que se puede liberar tras reanudar y que una preparación con huella diferente se conserva.

| Fase de la retención manual | Bytes lógicos del almacén | Bytes asignados del almacén | Bytes asignados en cuarentena |
|---|---:|---:|---:|
| Antes | 536.112 | 571.600 | 0 |
| En cuarentena | 537.696 | 572.384 | 131.856 |
| Tras deshacer | 538.596 | 572.480 | 880 |
| Segunda cuarentena | 540.180 | 573.264 | 132.736 |
| Tras purgar | 409.172 | 442.256 | 1.728 |

La segunda purga liberó 131.008 bytes netos de archivos. Una réplica dañada y una réplica válida pero contradictoria bloquearon la purga y conservaron los bytes en cuarentena. `--retention` comprobó también el rechazo ante operaciones pendientes. La restauración tras deshacer dio `97a96f09e719a497e5d7be928e5a11e8f109ff51ea48a1ca57f8a0bca57390f1`. Los puntos retenidos después de purgar dieron `95688e7f258d76d439955e06f8dd111332f35c02bd3444e3dd5a2a2d2039495f` y `e46828583ebf8a48abb4b3db503bb2c04816e8663d7c3ea97172929b56c86772`.

## Distribución local

Dos compilaciones Windows x64 desde extracciones independientes de `6f782af214416e59ab796c7daef5730255d571b8` produjeron los mismos 500 hashes y ZIP idénticos. `Test-Portable.ps1` comprobó ambas compilaciones y la extracción final. Los ensayos de versiones, restauración, reconciliación sin avisos y compatibilidad con los tres modos de `335a98a` pasaron desde el motor extraído. También pasó `--product-monitor-cross-volume-smoke D:\` en los tres modos, con retorno del destino, rechazo del sustituto, pausa durable y recuperación sin origen. La comprobación adicional de catálogos contradictorios usa el arnés del repositorio y el mismo binario extraído.

| Artefacto | SHA-256 |
|---|---|
| `FolderTwin-win-x64-6f782af.zip` | `da80446b9f2960d06331b61e1efb117ba391a047337273e2b8d150cf4ba140e3` |
| `SHA256SUMS.txt` | `d14e3c2fc72f740ba06b0c356efbc5026fcedd56a69145a81aedc3a427ca7222` |

El ensayo de capacidad usó presupuestos y errores inyectados. La prueba de VHDX de `598fbc7` y el piloto personal conservan su validación independiente.
