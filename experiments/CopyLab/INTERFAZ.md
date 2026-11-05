# Revisión del escritorio de producto

La ventana de producto sigue siendo una vista previa. Esta evidencia cubre la distribución del panel y parte del recorrido de escritorio. V1-10 y las comprobaciones de accesibilidad EXT-05A, EXT-05B y EXT-05C conservan sus propios criterios.

El paquete ofrece `FolderTwin.exe` como entrada pública. Sin argumentos abre los trabajos de producto; el ejecutable interno `CopyLab.exe` conserva sus protocolos y también abre producto sin argumentos. **Respaldos de alfa** y el argumento de desarrollo `--lab` dan acceso explícito al laboratorio. `--product-startup-smoke` está restringido al runner Windows hospedado: abre el ejecutable público sin argumentos con un perfil vacío, comprueba el título de trabajos y su cierre sin crear trabajos. Este ensayo comprueba el arranque, no todo el recorrido de instalación.

La ventana de producto, el ejecutable y la bandeja usan `Assets/FolderTwin.ico`, con seis tamaños entre 16 y 256 píxeles. La geometría propia está en `Assets/FolderTwin.svg`; `scripts/New-AppIcon.ps1` exporta el ICO en Windows. La exportación repetida produjo los mismos bytes en el entorno de desarrollo. La distribución usa el recurso versionado, sin generarlo durante la compilación. La bandeja libera su copia del icono al cerrarse. La comprobación de escalado de Windows sigue pendiente.

## Sesión sintética reproducible

Después de compilar, `CopyLab.exe --product-synthetic-preview` crea una sesión desechable, tres trabajos y sus respaldos sintéticos. No abre el registro habitual del usuario. `--product-synthetic-preview compact` abre con 600 × 500 unidades de WPF y `--product-synthetic-preview light` fuerza el tema claro solo en esa ventana. El tema normal sigue el sistema. No se instalan horarios al crear esta sesión.

Cerrar la ventana retira el fixture después de comprobar su marcador. Si queda un ejecutor activo o un handle impide retirarlo, conserva la sesión para revisión. Nunca elimina otros espacios temporales ni los respaldos del usuario. Estos argumentos son herramientas de desarrollo, no el flujo normal de producto.

`FolderTwin.exe --product-synthetic-preview empty` abre el paquete portable con un registro de trabajos vacío. Crea `origen`, `destinos` y `recuperaciones` en el espacio desechable, con `Prueba.txt` y una carpeta vacía en el origen. Los selectores nativos de carpetas empiezan en ese espacio y solo aceptan subcarpetas directas. Los trabajos y las instalaciones de ensayo quedan aislados del perfil habitual. La prueba de horarios nativos se ejecuta por separado en CI.

En Windows 11, el paquete local de desarrollo abrió vacío en tema oscuro. Se creó un trabajo de versionado con los selectores nativos. Enter avanzó al destino, enfocó el nombre y guardó desde la revisión. Un escritor abierto sobre `Prueba.txt` produjo una incidencia por ruta y la fecha de último respaldo quedó vacía. Después de liberar el archivo, el reintento confirmó el punto. **Verificar respaldo** terminó bien y **Recuperar en carpeta nueva** restituyó el archivo y la carpeta vacía. El SHA-256 del archivo recuperado y su original coincidió en `9a52172734bb9222e856377e058051db9bda3d596c580d45a1276da4f75e8c51`.

Una segunda sesión vacía del paquete local con el código de `3128747` creó acumulativo y espejo desde los mismos selectores. Enter y Tab recorrieron el asistente y las flechas cambiaron el modo. En acumulativo se completaron copia, verificación y restauración. En espejo se observó la revisión de un archivo y una carpeta nuevos antes de aplicar, luego se verificó y restauró el punto. Cada modo creó una salida distinta con el mismo SHA-256 y la carpeta vacía. Las acciones de recuperación se mantuvieron visibles durante la revisión. El SHA-256 del manifiesto de ese paquete local fue `a12a6e2670d31bd63e56795774cfe2c4262aaa14ee9efa5331aba67aaf93b291`. Este recorrido usó configuración aislada y horarios a demanda.

La revisión del espejo inicialmente aparecía después de retención y ajustes. Ahora está junto a las acciones del trabajo. En el paquete local de `050da68` se observaron el resumen y **Aplicar espejo revisado** sin desplazar el panel. Se repitió además el bloqueo de `Notas.txt`, el aviso en español, **Reintentar respaldo** y la conservación del intento fallido en el historial. La ruta aparecía dos veces en la entrada del historial. La presentación usa ahora el mismo formato de resultado para el pie y el historial, con una sola línea para la ruta.

En ese recorrido se corrigieron dos problemas de presentación. El aviso de archivo en uso mostraba el error interno en inglés y ahora indica en español cómo reintentar. Las acciones de recuperación quedaban debajo de la lista y se desplazaban al revisar una salida. Ahora la lista se desplaza por separado y las acciones permanecen en el pie del panel. Los arneses cubren el aviso y el pie a tamaño normal y mínimo. La navegación con lector, el escalado de Windows y el recorrido desde un perfil real limpio siguen abiertos.

## Observación del 5 de noviembre de 2026

La inspección mediante Computer Use encontró que los detalles del trabajo consumían casi toda la altura del panel anterior y dejaban la lista recortada. Se cambió a lista y detalles en dos columnas, apilados por debajo de 900 unidades de ancho. Cada panel desplaza su propio contenido. El pie mantiene progreso y controles cuando hay un proceso activo; las fechas e información del trabajo están en los detalles.

En el escritorio local se comprobaron los tres trabajos visibles a tamaño inicial, selección, cambio entre trabajos, paso con Tab de la lista a **Respaldar ahora**, foco visible, tema claro y oscuro y lectura de nombres y descripción de los trabajos mediante UI Automation. En la ventana mínima se navegó al último trabajo con End y se abrió recuperación. Los detalles y la recuperación requieren desplazamiento en ese tamaño. Se reservó altura para una fila completa de la lista.

Los contenedores de trabajos y archivos recuperables exponen sus etiquetas en lugar del nombre de su tipo interno. Las fechas y cuarentenas usan sus etiquetas accesibles. Actualizar conserva el trabajo seleccionado. El tema claro del fixture usa la API de WPF fijada por el SDK; su diagnóstico experimental está suprimido únicamente en esa línea de desarrollo.

En **Aplicación** se corrigió el estado que quedaba en «Comprobando la selección…» y se establece el foco lógico y de teclado tras mostrar el panel. En la sesión sintética del escritorio se abrió mediante Alt+P, se observó el foco visible en la aceptación, Espacio habilitó el botón y Tab pasó a **Instalar o actualizar ejecutor**. No se ejecutó la instalación mediante esta inspección. El arnés WPF comprueba el foco al abrir el panel. Esta comprobación no sustituye el recorrido con lector de pantalla.

## Regresión y límites

`--product-smoke` abre una ventana real y comprueba que haya al menos una fila completa y detalles accesibles a 1040 × 740, 760 × 620 y 600 × 500. Comprueba los nombres de trabajos con el peer de UI Automation y la conservación de la selección después de actualizar. Mantiene los recorridos de copia, restauración, planes pendientes, retención, configuración y trabajadores existentes.

Faltan el recorrido completo desde instalación limpia del paquete final, la revisión del asistente y de todos los estados de error en el escritorio, lector de pantalla, alto contraste, escalado y cambios de DPI entre monitores. Cambiar el tamaño de una ventana no equivale a probar el escalado de Windows. Las observaciones de esta sesión no deben trasladarse como aprobaciones a esos criterios externos.
