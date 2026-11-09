# Evidencia local de la candidata de automatización

## Candidata `3cb9c6a` en aceptación

El código `3cb9c6a22146c8928f2ac1ed9449090e9a366e80` conserva el reemplazo acotado acumulativo cuando quedan archivos exclusivos del destino. Dos compilaciones independientes, a partir de árboles extraídos del mismo archivo de fuentes, produjeron los mismos 500 hashes y ZIP idénticos. La extracción pasó `Test-Portable.ps1`. Las pruebas funcionales desde el ZIP y la medición con 329.000 archivos y un exclusivo siguen abiertas.

| Archivo | SHA-256 |
|---|---|
| `FolderTwin-win-x64-3cb9c6a.zip` | `3ddba023c2ff49203e3aa57ce1fa6ded982e66fbdf5103a49afd6af548acca12` |
| `SHA256SUMS.txt` | `f49822fdfa6bc955bb16db6b5e4d79e7c62753fa6979c24c7ece9cd13d505c94` |

Para repetir el ZIP, usa los archivos de la compilación portable en orden ordinal por ruta relativa con `/`, compresión `Optimal` y fecha fija de entrada `2000-01-01 00:00:00` con desplazamiento cero. El informe se conserva fuera del ZIP que describe. Las comprobaciones ejecutadas desde la extracción anterior `9fa0c63` aprobaron el asistente, C: → D: y la reconciliación tras perder avisos. El ensayo de arranque sin argumentos exige un runner desechable. El ensayo nativo lanzado desde la sesión de herramientas agotó la espera sin observar el monitor. Se conserva esa incidencia para la comprobación en una tarea temporal.

## Paquete anterior `c8a413a`

El paquete `c8a413a760c9768480e1223344694c12e5797c79` es para ensayos con datos sintéticos en Windows 11 x64. Usa .NET 10.0.14 incluido. El informe acompaña al paquete y se conserva aparte.

| Archivo | SHA-256 |
|---|---|
| `FolderTwin-win-x64-c8a413a.zip` | `a0743529ba2771f1b0bfbb7c53dc286c060b9fda735f2fccd4135e2130b582f7` |
| `SHA256SUMS.txt` | `41a8e54baebcb7eece78350d87c27bae2c6c0e6ed73244ba476eeaa030ed41c0` |

Dos compilaciones limpias produjeron los mismos 500 hashes. Dos ZIP con orden ordinal y una fecha fija de entrada coincidieron byte a byte. La extracción volvió a pasar `Test-Portable.ps1`, que verifica identidad, fuentes, inventario, licencias y guías.

### Verificaciones del paquete

| Prueba | Resultado observado |
|---|---|
| `--product-automation-ui-smoke` | Creó los tres modos desde Ruta, inició la primera copia al guardar y procesó cambios con la ventana cerrada. Verificó pausa durable y restauró bytes con el origen apartado. |
| `--product-monitor-cross-volume-smoke` | Usó carpetas desechables en dos discos NTFS locales, C: → D:. Probó retorno de la carpeta registrada, rechazo de una sustituta con marcador copiado y restauración sin origen. Los despachos aislados tomaron entre 3,00 y 3,17 s. |
| `--product-monitor-reconciliation-smoke` | Recuperó los bytes tras suprimir callbacks y tras inyectar el error que representa un desbordamiento. |
| `--product-monitor-schedule-soak` | El Programador lanzó un monitor real. En 120,02 s de reposo usó 0,40 % de un núcleo y 73.818.112 bytes residentes. Tras terminar el monitor, Windows lanzó otro y se restauró la edición hecha durante la interrupción. |
| `--product-monitor-update-smoke` | Pasó del ejecutor de `c70ac2c` al nuevo con el monitor activo, conservó el inicio al entrar en Windows y comprobó el respaldo y la restauración después de la retirada. |
| `--product-smoke` desde el ZIP extraído | Aprobó instalación, retirada y reinstalación, los tres modos, teclado y temas, trabajadores sin ventana, recuperación, importación, bandeja y retención. |

La matriz de automatización registra también los resultados de cada revisión anterior, incluidos el recorrido WPF de instalación y retirada de `3b24a79`, la auditoría de lectores, el disco lleno inyectado, la terminación de trabajadores, los catálogos y la retención. [Matriz de automatización](AUTOMATIZACION.md).

### Cierre pendiente en esa compilación

Al cerrar esta compilación seguía abierto el rendimiento a gran escala. El espejo del motor anterior confirmó y restauró la edición de 329.000 archivos en 920,57 s frente al presupuesto de 60 s. Las mediciones posteriores se registran en la [matriz de automatización](AUTOMATIZACION.md#presupuesto-de-rendimiento). El arnés de volumen lleno y cambio de letra necesita un Windows aislado con permisos para montar un VHDX. Las pruebas con carpetas entre discos y los fallos inyectados tienen su propia evidencia.

El piloto de 7–14 días, USB físico, bloqueo y ciclos de sesión de Windows, reinicio, suspensión, otra cuenta o equipo, nube real, lector de pantalla y escalado real quedan pendientes. Usa el ZIP solo con datos sintéticos y conserva los respaldos personales aparte. [Protocolo del piloto](PILOTO.md).
