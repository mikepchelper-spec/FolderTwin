# Crecimiento y capacidad del respaldo automático

## Línea base de `335a98a`

Se verificó el paquete de Windows x64 y sus 500 archivos antes de probar. Windows 11, compilación 26100, con origen sintético en SSD NVMe C: y destinos dedicados en HDD SATA D:. El monitor del paquete vigiló los tres modos en un registro nuevo, con la interfaz cerrada y un ejecutor a la vez. Los árboles de acumulativo y espejo se prepararon y verificaron antes de iniciar la vigilancia. Versionado hizo su primera copia en el monitor. Los 4.096 archivos de cada trabajo usan 32 contenidos pequeños repetidos. Las fases se dispararon tras observar vigilancia y se comprimieron a minutos. La comparación periódica fue de 60 s y la espera ordinaria de cambios se mantuvo en 2 s. La ráfaga usó ocho guardados por trabajo, separados por 180 ms.

El arnés `--storage-growth` mide desde la edición hasta el inicio y resultado registrados por cada ejecutor. Entre fases inventaría el destino y los intentos de cada trabajo en C:. El tamaño lógico y la asignación por archivo provienen de `FILE_STANDARD_INFO`. La suma de asignación excluye índices de directorio, MFT, metadatos de volumen y tráfico de escritura que no queda en archivos. [Asignación de archivos en Windows](https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-file_standard_info).

| Modo | Puntos iniciales → finales | Árbol actual final | Objetos finales | Catálogo + réplica final | Diarios finales | Historial final en C: |
|---|---:|---:|---:|---:|---:|---:|
| Acumulativo | 1 → 6 | 49.191 B | 447 B | 10.964.202 B | 10.994.286 B | 10.433 B |
| Versionado | 1 → 7 | 0 B | 447 B | 12.789.212 B | 6.409.338 B | 11.935 B |
| Espejo protegido | 1 → 7 | 49.167 B | 447 B | 12.788.978 B | 11.909.148 B | 12.146 B |

Los tamaños de esta tabla son lógicos. La cuarentena estaba vacía. El reposo añadió intentos sin cambios y conservó un punto por trabajo. Acumulativo conservó el archivo borrado y no añadió un punto en esa fase. El primer cambio de 12 bytes añadió aproximadamente 0,91 MB por réplica en los tres modos, unos 0,91 MB de descriptor en versionado y unos 1,83 MB de diarios en acumulativo y espejo. El último guardado de la ráfaga se recuperó con el origen apartado en los tres modos. Su SHA-256 fue `6362f0cc70fc59adf788c84fbe199735bf46a263bfd966be9468ecf2f96670a9`.

En la ráfaga, las confirmaciones desde la primera escritura tardaron 12,072 s en acumulativo, 21,325 s en espejo y 26,476 s en versionado. Los despachos ocurrieron a 4,444 s, 13,533 s y 22,676 s, respectivamente. Los tiempos incluyen la espera del monitor por el trabajo anterior.

La prueba de 329.000 archivos del mismo ZIP usa versionado para medir el coste de los catálogos en una carpeta grande. Su primer punto ocupó 146.695.392 bytes lógicos y 146.702.336 asignados entre las dos réplicas. Los diarios sumaron 73.353.996 bytes lógicos y 73.364.152 asignados. Los objetos de datos solo sumaron 384 bytes lógicos porque el conjunto repite 32 contenidos. Los tamaños de otros modos y cargas dependen de sus inventarios y datos.

## Capacidad y retención en la línea base

El arnés `--storage-safety` llamó la comprobación de reserva al iniciar el análisis con disponibilidad sintética cero. Los tres modos rechazaron el intento antes de publicar. El resultado durable usó el código genérico `0x80131620` y habló de una reserva del laboratorio. Ruta solo reconocía como falta de espacio el error nativo, por lo que ambos fallos se mostraban de forma distinta. Al inyectar `0x80070070` durante la publicación se conservó el punto previo, otro trabajo pudo confirmar y el intento posterior reanudó la misma generación. Los límites inyectados se aplicaron a las operaciones del arnés y la capacidad de los discos permaneció disponible.

En un almacén de versionado con tres puntos, cada fecha tenía un objeto exclusivo de 128 KiB y todas compartían otro. La cuarentena trasladó el objeto expirable dentro del mismo volumen y añadió 784 bytes de asignación por archivo al total. Tras deshacer, el punto antiguo volvió a restaurarse con el origen apartado. Una segunda cuarentena rechazó la purga antes de la gracia en la primera ronda y permitió la purga posterior con el reloj de prueba avanzado. La purga liberó 131.008 bytes netos de asignación de archivos. Los dos puntos retenidos pasaron verificación y restauración con el origen apartado.

## Cambios a comprobar

El rechazo previo por reserva insuficiente usa el mismo código durable de disco lleno que el fallo de publicación. La interfaz conserva la fecha del último respaldo completo, muestra la acción en Ruta e historial y abre primero el trabajo con falta de espacio desde el aviso de bandeja. Versionado comprueba antes de copiar que cabe el tamaño lógico mínimo del descriptor y las dos réplicas, además de la reserva. La comprobación inicial de espejo automático contempla los cuatro documentos que coexisten antes de confirmar la actualización. Los objetos y la asignación real pueden elevar el espacio necesario. El manejo del fallo de escritura sigue siendo necesario si el espacio cambia durante una copia.

Cuando acumulativo o espejo termina de aplicar una actualización automática, puede soltar una lista temporal de preparación si su huella coincide con la del punto y el descriptor durable confirma esa misma huella e identidad. Conserva el plan y los estados del diario. Una actualización pendiente, un inventario distinto o un documento que no coincide con su huella conserva su preparación. Se mantienen las dos réplicas y el proceso manual de retención. La corrección reduce el espacio que queda ocupado después de la publicación. La preparación sigue escribiéndose antes de aplicar para poder reanudar una interrupción.
