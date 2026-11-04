# FolderTwin

*Copias locales. Versiones recuperables.*

App sencilla de respaldo para Windows: elegir origen, destino y cómo conservar los archivos. Sin cuenta, suscripción, servidor ni conexión obligatoria. Estado actual: **prueba técnica inicial; aún no hay aplicación de respaldo utilizable**.

## Leer primero

Este proyecto está dedicado al respaldo cotidiano local de carpetas.

- [Plan de trabajo](PLAN.md): experiencia, arquitectura, prioridades, pruebas y límites.
- [Investigación](INVESTIGACION.md): necesidades observadas, documentación técnica y fuentes.
- [Nombres](NOMBRES.md): elección, alternativas anteriores y estado de la comprobación de disponibilidad.

## Decisión propuesta

C# + .NET 10 LTS + WPF con tema Fluent, APIs de archivos de Windows y Programador de tareas. Una interfaz y un ejecutor sin ventana comparten la misma lógica. Sin Electron, servicio remoto ni IA necesaria para funcionar. SQLite en disco fijo local solo para índice/estado; objetos, manifiestos JSONL duplicados y diario recuperable en el destino.

Primero comprobar copia y restauración en carpetas desechables. Después versionado con objetos por contenido SHA-256 y programación. El espejo y la limpieza se incorporan cuando sus protecciones hayan pasado pruebas de interrupción. La interfaz sencilla no debe ocultar fallos ni archivos omitidos; los archivos solo disponibles en la nube no se descargan automáticamente.

Con el SDK de .NET 10, ejecuta `dotnet run --project experiments/CopyProbe` en Windows. El experimento genera sus propios datos temporales y comprueba copia, flush, verificación SHA-256 y restauración. Incluye fallos inyectados, archivos ocupados, corrupción y recuperación de una copia interrumpida a partir de su registro en disco. GitHub Actions ejecuta las mismas comprobaciones. El alcance actual se limita a datos sintéticos en un volumen.

Añade `-- --compare` para comparar el flujo de .NET, CopyFile2 y Robocopy sobre el mismo conjunto sintético. Consulta el [resultado y sus límites](PLAN.md#resultado-de-la-comparación-de-transferencia).

Usa `-- --uncached` para comparar la verificación con y sin la caché de Windows, o `-- --paths` para probar la selección de una raíz local, la sustitución de carpetas, la reapertura de un destino registrado y la recuperación con nombres Unicode en rutas largas. [Resultados del laboratorio](PLAN.md#fallos-de-es-rutas-y-verificación-sin-caché).

Usa `-- --inventory` para comprobar el inventario de metadatos de carpetas sintéticas. Distingue un origen vacío de uno inaccesible y marca como incompleto el análisis con omisiones, enlaces, cancelación o límites alcanzados. El inventario está acotado a 10.000 entradas y 64 niveles por defecto. [Alcance del inventario](PLAN.md#inventario-y-revisión-previa).

Usa `-- --batch` para copiar y restaurar un lote sintético con subcarpetas, un archivo vacío y una carpeta vacía. Comprueba resultados parciales, reintentos, cambios después del análisis y conflictos en destino. [Alcance y límites del lote](PLAN.md#lotes-sintéticos-y-resultados-parciales).

Usa `-- --catalog` para probar los catálogos JSONL duplicados, su confirmación y la restauración desde otro proceso con el origen ausente. El lector rechaza catálogos truncados, rutas inseguras y réplicas contradictorias. Una réplica íntegra de un lote confirmado permite restaurar con un aviso sobre la copia dañada. [Formato experimental](experiments/CopyProbe/CATALOG.md).

Usa `-- --resume` para terminar trabajadores de prueba y reabrir sus lotes pendientes desde otros procesos. Reconoce lo ya verificado, recupera temporales con registro válido y deja pendientes los archivos que aún necesitan el origen. Los datos sin verificar quedan para revisión. [Descriptor de ejecución](experiments/CopyProbe/PENDING.md).

Para abrir la ventana de laboratorio en Windows, ejecuta `dotnet run --project experiments/CopyLab`. Elige **Origen** y **Destino**, pulsa **Analizar carpetas** y revisa los tres archivos previstos. Después usa **Copiar y verificar** y **Restaurar y verificar**. La ventana muestra los resultados por ruta y ofrece **Reintentar copia** si el lote queda parcial. Declara la copia completa después de confirmar el catálogo.

Durante una copia, **Cancelar copia** solicita detener el lote en el próximo punto seguro. Si un archivo está en curso, el motor termina ese paso y conserva lo ya verificado para el reintento. Si la confirmación del catálogo ya empezó, el lote puede terminar.

Durante la copia, el reintento, la restauración y la verificación, la ventana muestra el archivo en curso y los recuentos de archivos procesados y verificados. El resultado final aparece al terminar las comprobaciones.

**Pausar copia** espera al próximo límite entre archivos. La ventana indica cuándo el lote queda en pausa y permite continuarlo o cancelarlo.

La ventana muestra el nombre de la sesión y conserva su carpeta temporal si iniciaste la copia. En otra ejecución, pulsa **Abrir sesión anterior** y elige esa carpeta `FolderTwin-Batch-…`. El laboratorio ofrece reintentar un lote pendiente o restaurar uno confirmado. Al restaurar una sesión anterior, muestra la ruta de una carpeta de prueba nueva que se retira al cerrar. CI comprueba estos recorridos con `-- --smoke`.

En un lote confirmado, pulsa **Verificar respaldo** para comprobar las carpetas y comparar el tamaño y SHA-256 de cada archivo con el catálogo. La ventana señala las rutas ausentes, alteradas o que no pudo leer. La comprobación es de solo lectura sobre el respaldo.

El laboratorio admite el flujo principal de cada archivo. Si encuentra flujos de datos alternativos de NTFS en un archivo o carpeta, lo indica para revisión y conserva el contenido existente.

Abre **Historial de intentos** para ver las últimas 20 copias, restauraciones o verificaciones de la sesión, incluidas las incidencias de intentos anteriores. El historial guarda nombres relativos del lote sintético y categorías de incidencia en la carpeta de la sesión. Si hay registros ilegibles o falla la escritura del historial, la ventana lo indica aparte del resultado de la operación.

Si un archivo está en uso, el laboratorio intenta mostrar las aplicaciones que detecta Windows mediante Restart Manager. Si no puede identificarlas, conserva el aviso y permite reintentar. El diagnóstico no cierra aplicaciones.

Para repetir el ensayo de disco lleno, cambio de letra, destino sustituido y reconexión ordenada, ejecuta **Disposable volume lab** en la pestaña Actions de GitHub. Usa un disco virtual de 128 MiB en un runner Windows desechable. El ensayo de corte eléctrico en hardware queda aplazado. [Alcance y resultados](PLAN.md#laboratorio-de-volumen-desechable).

La revisión de opiniones externas incorpora diagnóstico de archivos en uso, recuperación por carpetas/fecha, bandeja y cuarentena previa al borrado. Las optimizaciones de E/S y consumo se deciden mediante pruebas. El plan distingue medidas de protección de garantías todavía no demostradas.

Este trabajo no sustituye el respaldo existente: no usar prototipos con datos personales.

## Licencia

El proyecto se distribuye bajo [GNU GPL versión 3](LICENSE).
