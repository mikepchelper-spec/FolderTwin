# Evidencia local de la candidata de automatización

## Candidata `3cb9c6a` en aceptación

El código `3cb9c6a22146c8928f2ac1ed9449090e9a366e80` conserva el reemplazo acotado acumulativo cuando quedan archivos exclusivos del destino. Dos compilaciones independientes, a partir de árboles extraídos del mismo archivo de fuentes, produjeron los mismos 500 hashes y ZIP idénticos. La extracción pasó `Test-Portable.ps1` y los tres modos cumplieron el presupuesto de edición a 329.000 archivos.

| Archivo | SHA-256 |
|---|---|
| `FolderTwin-win-x64-3cb9c6a.zip` | `3ddba023c2ff49203e3aa57ce1fa6ded982e66fbdf5103a49afd6af548acca12` |
| `SHA256SUMS.txt` | `f49822fdfa6bc955bb16db6b5e4d79e7c62753fa6979c24c7ece9cd13d505c94` |

Para repetir el ZIP, usa los archivos de la compilación portable en orden ordinal por ruta relativa con `/`, compresión `Optimal` y fecha fija de entrada `2000-01-01 00:00:00` con desplazamiento cero. El informe se conserva fuera del ZIP que describe. La extracción anterior `9fa0c63` aprobó el asistente, C: → D: y reconciliación. Su ensayo nativo agotó la espera desde la sesión de herramientas. Las pruebas nativas de `3cb9c6a` se ejecutaron mediante una tarea temporal en la sesión interactiva. El ensayo de arranque sin argumentos en un perfil vacío sigue reservado para un runner desechable.

La copia local está en `C:\FolderTwin-Distribucion\FolderTwin-3cb9c6a`. El ZIP junto a esa carpeta mantiene el SHA-256 indicado y la extracción local volvió a pasar `Test-Portable.ps1`. Esta revisión fue el destino del acceso **FolderTwin candidata** hasta verificar el traspaso a `598fbc7`.

| Verificación de `3cb9c6a` | Resultado observado |
|---|---|
| Asistente de Ruta desde el ZIP extraído | Creó los tres modos automáticos, inició el primer respaldo al guardar y procesó una edición con la ventana cerrada. Comprobó la pausa persistente y restauró bytes con el origen apartado. |
| Monitor C: → D: desde el ZIP extraído | Despachó una edición aislada en 2,506–2,521 s y confirmó en 3,254–3,945 s desde el cambio, según el modo. Comprobó exclusión de un segundo monitor, desaparición y retorno del destino, pausa y restauración sin origen. En acumulativo comprobó otra edición acotada después de que un archivo quedara solo en el destino y restauró ambos contenidos. |
| Reconciliación desde el ZIP extraído | Restauró los bytes de dos ediciones con el origen apartado, una tras suprimir callbacks reales y otra tras inyectar el desbordamiento del vigilante. |
| Interfaz y ciclo del paquete | `--product-smoke` aprobó instalación, retirada, reinstalación, trabajo y restauración en los tres modos, incidencias, navegación por teclado, ayuda, temas, bandeja, importación y retención. El ensayo usa un perfil desechable con selectores y programación inyectados. |
| Trabajadores reales y fallos sintéticos | `--product-runs` terminó trabajadores manuales y automáticos y recuperó los tres modos. Comprobó pausa, cancelación, concurrencia de disparadores y secuencia ante cambios de reloj. `--product-auto-partial` comprobó recuperación parcial y disco lleno inyectado. `--product-auto-mirror` comprobó recuperación sin origen, límites de retiradas, conflictos, guardados mediante temporal y alternativas de comparación. `--tray-audit` mantuvo separadas las incidencias de respaldo y verificación. |
| Controles mientras el destino está ausente | Un arnés vinculado al motor extraído lanzó el monitor real para los tres modos. Probó por separado pausa, desactivación y retirada mientras el destino de D: estaba apartado. El retorno del destino y el arranque de otro monitor no generaron nuevos intentos. La reactivación explícita respaldó la edición pendiente y conservó el punto anterior y la incidencia de ausencia. |
| Movimiento y renombrado de subárbol | El arnés movió al origen un árbol de 256 archivos y verificó la restauración con el origen apartado. Al renombrar la raíz, acumulativo conservó ambas rutas y versionado guardó la ruta nueva en el punto nuevo. Espejo protegió el árbol anterior y pidió revisión por el alcance del renombrado. El arnés distingue los archivos restaurados de sus recibos y bloqueos internos. |
| Compatibilidad desde el ZIP público de RC2 | El arnés creó trabajos, puntos, exportaciones e historial con el motor `d88d3b9d4f35b07aa80c1dbc7d90c41f0aa462f3d27f6fce75b92e7168cb2a05`. El motor de `3cb9c6a`, `77791939f6b719c1c9b3d9880cfe4113b979eda78fce74be0e64d867a4f77c5b`, abrió y verificó los tres modos y restauró el árbol y los bytes con el origen apartado. El hash del binario anterior coincide con su entrada en el ZIP público. |
| Letras temporales en dos volúmenes NTFS | Se registraron los tres modos desde un alias de la carpeta desechable de D:. Se trasladó el alias legítimo a otra letra y se reutilizó la primera para un sustituto en C: con un marcador copiado. El motor del paquete informó la ausencia del destino registrado mientras su carpeta estaba apartada, dejó intacto el sustituto y restauró el cambio al volver el destino legítimo. Los alias se retiraron. El ensayo usa alias de sesión y deja pendiente la reasignación de una partición y el remontaje VHDX. |
| Escala acumulativa desde el ZIP extraído | Confirmó en 20,076 s una edición de igual tamaño y fecha entre 329.000 archivos con un exclusivo en el destino. Restauró ambos archivos con el origen apartado. La comparación sin cambios mantuvo el catálogo anterior y la edición añadió una sola generación. [Contadores y escala](AUTOMATIZACION.md#medición-del-reemplazo-acotado). |
| Escala de versionado desde el ZIP extraído | Confirmó la edición de igual tamaño y fecha en 0,871 s a 4.096 archivos y en 31,246 s a 329.000 archivos. Restauró los bytes correctos con el origen apartado. La comparación sin cambios mantuvo una generación y la edición añadió exactamente una. [Contadores y escala](AUTOMATIZACION.md#presupuesto-de-rendimiento). |
| Escala de espejo desde el ZIP extraído | Confirmó la edición de igual tamaño y fecha de 329.000 archivos en 24,816 s y restauró los bytes correctos en 2,800 s con el origen apartado. La comparación sin cambios conservó una generación y la edición añadió exactamente una. [Contadores y escala](AUTOMATIZACION.md#medición-del-reemplazo-acotado). |
| Actualización desde `c8a413a` | Una tarea temporal de la sesión interactiva ejecutó `--product-monitor-update-smoke` con código 0. El Programador inició el monitor, lo sustituyó tras una terminación real y procesó el cambio ocurrido durante la caída. La actualización traspasó el monitor al ejecutor nuevo, conservó el inicio al entrar en Windows y verificó la retirada y restauración sin origen. |
| Reposo y recuperación nativa | `--product-monitor-schedule-soak` completó con código 0. En 120,01 s midió 0,25 % de un núcleo, 74.248.192 bytes residentes, 6.267 bytes leídos y 696 escritos. El monitor no despachó durante el reposo. El arnés lo terminó, observó otro proceso, recuperó la edición hecha durante la caída y retiró la tarea. |
| Observación prolongada del paquete | `--product-monitor-soak` terminó en 278,52 s. Cada modo procesó 64 guardados por reemplazo con temporal y recuperó el contenido exacto del último. El primer despacho de cada ráfaga tomó entre 20,720 y 20,880 s. Los tres modos restauraron por SHA-256 un archivo de 32 MiB con el origen apartado. El ensayo también verificó reposo, reconexión, pausa, un segundo trabajo y el rechazo del destino sustituido. |

El arnés del motor confirma la edición de igual tamaño y fecha y restaura una muestra con el origen apartado. Estas cifras excluyen el despacho del monitor y la preparación inicial del árbol.

| Archivos | Límite | Acumulativo | Versionado | Espejo protegido |
|---:|---:|---:|---:|---:|
| 4.096 | 15 s | 0,712 s | 0,871 s | 0,835 s |
| 329.000 | 60 s | 20,076 s | 31,246 s | 24,816 s |

La rama local conserva la candidata sin subir a GitHub. La validación pública de Windows `42212336681` terminó correctamente para RC2 (`d902f4c`). La evidencia de `3cb9c6a` procede de este paquete local. [Validación pública de RC2](https://github.com/mikepchelper-spec/FolderTwin/actions/runs/42212336681).

### Paquete con el arnés de auditoría corregido

La auditoría XML `--schedule-policy-audit` incluida en `3cb9c6a` falla al construir su fixture sintético porque usaba la firma anterior de un constructor privado. La revisión `598fbc7` corrige esa llamada en el código de pruebas. Dos compilaciones independientes produjeron ZIP idénticos. La extracción aprobó los 500 hashes, la auditoría XML de siete alteraciones y el recorrido automático de Ruta en los tres modos. `CopyProbe.dll` conserva exactamente el hash de `3cb9c6a`. Los cambios de manifiesto se limitan a `CopyLab.dll`, la identidad del paquete, las fuentes archivadas y cinco guías cuyos enlaces llevan la revisión de origen. Las pruebas nativas de la tabla anterior corresponden a `3cb9c6a`.

El ZIP de `598fbc7` se conserva en `C:\FolderTwin-Distribucion` con su manifiesto aparte. El acceso **FolderTwin candidata** apunta a `C:\FolderTwin-Distribucion\FolderTwin-598fbc7\FolderTwin.exe` sin argumentos. **FolderTwin RC2** conserva su destino propio.

| Archivo | SHA-256 |
|---|---|
| `FolderTwin-win-x64-598fbc7.zip` | `05c58508b888f05b041875614821f42c68de0c5a0f967ab0d00b145fd696a084` |
| `SHA256SUMS.txt` | `700bfb6c647d621849963f35534e8eb5abb6d339e0e9554cf842f981f2eefe6e` |

`598fbc7` aprobó `--product-monitor-update-smoke` con código 0 desde una tarea temporal nativa, usando `3cb9c6a` como paquete anterior. El Programador sustituyó al monitor terminado, el respaldo recuperó la edición hecha durante la caída, el traspaso preservó el inicio con Windows y la retirada conservó la restauración con el origen apartado.

El ejecutable público de `598fbc7` abrió Ruta sin argumentos y cerró con código 0. El ensayo comprobó antes que `LocalAppData` resolvía a una carpeta desechable al lanzar procesos con un entorno aislado. El registro de FolderTwin se creó allí y quedó vacío al cerrar. El arnés usa la cuenta interactiva actual y permite repetir la prueba sin tocar el registro habitual. [Arnés complementario](experiments/AutomationAcceptance/README.md).

Los ensayos de alias y controles durante la espera se pueden repetir contra el mismo motor y ejecutable empaquetados. [Arneses complementarios](experiments/AutomationAcceptance/README.md).

### Alcance del cierre

La aceptación local de `3cb9c6a` cubre creación desde Ruta, inicio automático al guardar, ventana cerrada, edición, retorno del destino, pérdida de avisos, caídas del monitor y de trabajadores, recuperación, retirada y reproducción del ZIP. `598fbc7` conserva el motor, incorpora la corrección del arnés XML y aprobó Ruta, arranque sin argumentos en un perfil aislado y traspaso nativo. El arnés `FolderTwin.exe --product-startup-smoke` sigue reservado para un runner hospedado con una cuenta Windows desechable completa.

Siguen pendientes las validaciones externas del piloto de 7–14 días, USB NTFS físico, bloqueo y ciclos reales de sesión, reinicio y suspensión, otra cuenta o equipo, nube real, lector de pantalla y DPI real. La observación automatizada de 278,52 s es evidencia sintética. [Validaciones externas](V1.md#validaciones-externas) · [Procedimiento de piloto](PILOTO.md).

### Comprobación de capacidad y letra pendiente en VHDX

El fallo inyectado y los alias de sesión pasaron desde `3cb9c6a`. El ensayo con un volumen NTFS realmente lleno, cambio de letra de la partición y remontaje sigue pendiente para este paquete. La sesión local carece de `SeManageVolumePrivilege`, que Windows requiere para montar el VHDX. La revisión automática bloqueó la descarga conjunta del emulador y de una imagen invitada de evaluación. [Requisito de Windows para montar discos virtuales](https://learn.microsoft.com/en-us/windows/win32/api/virtdisk/nf-virtdisk-attachvirtualdisk).

Punto de reanudación en un runner Windows hospedado y desechable. Usa `Test-VolumeCandidate.ps1` con el ZIP de `598fbc7` y los hashes de esta revisión. El arnés verifica la extracción y ejecuta el laboratorio de las fuentes archivadas en el mismo ZIP. Conserva el código nativo de disco lleno, la conservación de cada punto, el resultado tras reutilizar la letra y las verificaciones de restauración de los tres modos. La prueba crea un disco virtual propio de 128 MiB y comprueba su identidad antes de operar. [Comando y precondiciones](experiments/AutomationAcceptance/README.md) · [Definición del laboratorio](.github/workflows/volume-lab.yml).

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
