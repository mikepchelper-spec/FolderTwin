# Revisión del escritorio de producto

La ventana de producto sigue siendo una vista previa. Esta evidencia cubre la distribución del panel y algunos controles de accesibilidad; no aprueba por sí sola V1-10 ni EXT-05.

## Sesión sintética reproducible

Después de compilar, `CopyLab.exe --product-synthetic-preview` crea una sesión desechable, tres trabajos y sus respaldos sintéticos. No abre el registro habitual del usuario. `--product-synthetic-preview compact` abre con 600 × 500 unidades de WPF y `--product-synthetic-preview light` fuerza el tema claro solo en esa ventana. El tema normal sigue el sistema. No se instalan horarios al crear esta sesión.

Cerrar la ventana retira el fixture después de comprobar su marcador. Si queda un ejecutor activo o un handle impide retirarlo, conserva la sesión para revisión. Nunca elimina otros espacios temporales ni los respaldos del usuario. Estos argumentos son herramientas de desarrollo, no el flujo normal de producto.

## Observación del 5 de noviembre de 2026

La inspección mediante Computer Use encontró que los detalles del trabajo consumían casi toda la altura del panel anterior y dejaban la lista recortada. Se cambió a lista y detalles en dos columnas, apilados por debajo de 900 unidades de ancho. Cada panel desplaza su propio contenido. El pie mantiene progreso y controles cuando hay un proceso activo; las fechas e información del trabajo están en los detalles.

En el escritorio local se comprobaron los tres trabajos visibles a tamaño inicial, selección, cambio entre trabajos, paso con Tab de la lista a **Respaldar ahora**, foco visible, tema claro y oscuro y lectura de nombres y descripción de los trabajos mediante UI Automation. En la ventana mínima se navegó al último trabajo con End y se abrió recuperación. Los detalles y la recuperación requieren desplazamiento en ese tamaño. Se reservó altura para una fila completa de la lista.

Los contenedores de trabajos y archivos recuperables exponen sus etiquetas en lugar del nombre de su tipo interno. Las fechas y cuarentenas usan sus etiquetas accesibles. Actualizar conserva el trabajo seleccionado. El tema claro del fixture usa la API de WPF fijada por el SDK; su diagnóstico experimental está suprimido únicamente en esa línea de desarrollo.

## Regresión y límites

`--product-smoke` abre una ventana real y comprueba que haya al menos una fila completa y detalles accesibles a 1040 × 740, 760 × 620 y 600 × 500. Comprueba los nombres de trabajos con el peer de UI Automation y la conservación de la selección después de actualizar. Mantiene los recorridos de copia, restauración, planes pendientes, retención, configuración y trabajadores existentes.

Faltan el recorrido completo desde instalación limpia del paquete final, la revisión del asistente y de todos los estados de error en el escritorio, lector de pantalla, alto contraste, escalado y cambios de DPI entre monitores. Cambiar el tamaño de una ventana no equivale a probar el escalado de Windows. Las observaciones de esta sesión no deben trasladarse como aprobaciones a esos criterios externos.
