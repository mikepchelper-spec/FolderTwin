# FolderTwin

*Copias locales. Versiones recuperables.*

App sencilla de respaldo para Windows. La alfa permite elegir carpetas, copiar, verificar y restaurar datos sintéticos en un espacio temporal. El uso con datos personales queda para el piloto.

## Leer primero

Este proyecto está dedicado al respaldo cotidiano local de carpetas.

- [Plan de trabajo](PLAN.md): experiencia, arquitectura, prioridades, pruebas y límites.
- [Investigación](INVESTIGACION.md): necesidades observadas, documentación técnica y fuentes.
- [Nombres](NOMBRES.md): elección, alternativas anteriores y estado de la comprobación de disponibilidad.

## Alfa portable de Windows

Abre un resultado de [GitHub Actions](https://github.com/mikepchelper-spec/FolderTwin/actions/workflows/copy-probe.yml) para `main` y descarga el artefacto `FolderTwin-alpha-win-x64-…`. Extrae todo el contenido y abre `CopyLab.exe`. El paquete incluye .NET, el código fuente del commit y la [guía de la alfa](ALFA.md) para trabajar con datos sintéticos.

Para producir el paquete desde el código, instala el SDK indicado en [global.json](global.json) y ejecuta en PowerShell 7 para Windows:

```powershell
.\scripts\Publish-Alpha.ps1 -OutputDirectory "$env:TEMP\FolderTwin-alpha-win-x64"
```

Elige un nombre nuevo para cada compilación. El script guarda los archivos intermedios en una carpeta hermana con sufijo `.build`. La automatización compara los SHA-256 de todos los archivos tras dos compilaciones desde árboles limpios y ejecuta el recorrido WPF desde el paquete. La comparación cubre el contenido distribuido. GitHub genera el ZIP al entregar el artefacto.

## Decisión propuesta

C# + .NET 10 LTS + WPF con tema Fluent, APIs de archivos de Windows y Programador de tareas. Una interfaz y un ejecutor sin ventana comparten la misma lógica. Sin Electron, servicio remoto ni IA necesaria para funcionar. SQLite en disco fijo local solo para índice/estado; objetos, manifiestos JSONL duplicados y diario recuperable en el destino.

Primero comprobar copia y restauración en carpetas desechables. Después versionado con objetos por contenido SHA-256 y programación. El espejo y la limpieza se incorporan cuando sus protecciones hayan pasado pruebas de interrupción. La interfaz sencilla no debe ocultar fallos ni archivos omitidos; los archivos solo disponibles en la nube no se descargan automáticamente.

Con el SDK de .NET 10, ejecuta `dotnet run --project experiments/CopyProbe` en Windows. El experimento genera sus propios datos temporales y comprueba copia, flush, verificación SHA-256 y restauración. Incluye fallos inyectados, archivos ocupados, corrupción y recuperación de una copia interrumpida a partir de su registro en disco. GitHub Actions ejecuta las mismas comprobaciones. El alcance actual se limita a datos sintéticos en un volumen.

Añade `-- --compare` para comparar el flujo de .NET, CopyFile2 y Robocopy sobre el mismo conjunto sintético. Consulta el [resultado y sus límites](PLAN.md#resultado-de-la-comparación-de-transferencia).

Usa `-- --uncached` para comparar la verificación con y sin la caché de Windows, o `-- --paths` para probar la selección de una raíz local, la sustitución de carpetas, la reapertura de un destino registrado y la recuperación con nombres Unicode en rutas largas. [Resultados del laboratorio](PLAN.md#fallos-de-es-rutas-y-verificación-sin-caché).

Usa `-- --inventory` para comprobar el inventario de metadatos de carpetas sintéticas. Distingue un origen vacío de uno inaccesible y marca como incompleto el análisis con omisiones, enlaces, cancelación o límites alcanzados. El inventario está acotado a 10.000 entradas y 64 niveles por defecto. [Alcance del inventario](PLAN.md#inventario-y-revisión-previa).

Usa `-- --batch` para copiar y restaurar un lote sintético con subcarpetas, un archivo vacío y una carpeta vacía. Comprueba resultados parciales, reintentos, cambios después del análisis y conflictos en destino. [Alcance y límites del lote](PLAN.md#lotes-sintéticos-y-resultados-parciales).

Usa `-- --workspace` para probar trabajos con rutas y nombres de archivo libres en espacios temporales registrados por FolderTwin. Comprueba el espacio disponible, los rechazos antes de escribir, la reapertura de un lote parcial y la restauración sin el origen. Esta prueba genera y elimina sus propios datos sintéticos.

Usa `-- --versions` para probar dos generaciones con objetos SHA-256 compartidos. El ensayo crea sus propios datos, reabre generaciones pendientes, recupera un temporal verificado con el origen ausente, restaura ambas fechas y detecta la corrupción de un objeto común. [Formato de generaciones](experiments/CopyProbe/VERSIONES.md).

Usa `-- --catalog` para probar los catálogos JSONL duplicados, su confirmación y la restauración desde otro proceso con el origen ausente. El lector rechaza catálogos truncados, rutas inseguras y réplicas contradictorias. Una réplica íntegra de un lote confirmado permite restaurar con un aviso sobre la copia dañada. [Formato experimental](experiments/CopyProbe/CATALOG.md).

Usa `-- --resume` para terminar trabajadores de prueba y reabrir sus lotes pendientes desde otros procesos. Reconoce lo ya verificado, recupera temporales con registro válido y deja pendientes los archivos que aún necesitan el origen. Los datos sin verificar quedan para revisión. [Descriptor de ejecución](experiments/CopyProbe/PENDING.md).

Para abrir la ventana de laboratorio en Windows, ejecuta `dotnet run --project experiments/CopyLab`. Pulsa **Nuevo espacio temporal** y luego **Ver carpeta temporal**. En el Explorador, crea subcarpetas directas para el origen, el destino y la restauración. Coloca únicamente archivos sintéticos en el origen y deja vacías las otras carpetas. También están disponibles las carpetas de demostración **Origen**, **Origen ampliado** y **Destino** al iniciar la ventana.

Elige el origen y el destino y pulsa **Analizar carpetas**. La vista previa muestra el recuento, el tamaño de contenido y, para los trabajos configurables, el espacio disponible y la reserva. Elige **Copiar y verificar**. Si el lote queda parcial, revisa las incidencias y usa **Reintentar copia**. Tras la confirmación, puedes usar **Verificar respaldo** o **Restaurar y verificar**. El trabajo configurable permite elegir una subcarpeta vacía distinta para cada restauración.

En un espacio temporal, abre **Versiones** para usar una carpeta dedicada como almacén. Elige un origen sintético y crea un punto. Selecciona una fecha para recorrer sus carpetas, verificar los objetos o restaurar el punto o un archivo. El filtro de cambios compara la fecha seleccionada con la anterior. Si seleccionas un archivo eliminado, se recupera desde el punto anterior. Los puntos pendientes ofrecen **Reintentar punto**.

Durante una copia, **Cancelar copia** solicita detener el lote en el próximo punto seguro. Si un archivo está en curso, el motor termina ese paso y conserva lo ya verificado para el reintento. Si la confirmación del catálogo ya empezó, el lote puede terminar.

Durante la copia, el reintento, la restauración y la verificación, la ventana muestra el archivo en curso y los recuentos de archivos procesados y verificados. El resultado final aparece al terminar las comprobaciones.

**Pausar copia** espera al próximo límite entre archivos. La ventana indica cuándo el lote queda en pausa y permite continuarlo o cancelarlo.

Los espacios configurables se conservan al cerrar. Para retomarlos, pulsa **Abrir espacio**, elige la carpeta `FolderTwin-Workspace-…` y usa **Abrir trabajo** para seleccionar el destino. Para una sesión de los lotes de demostración, usa **Abrir demostración anterior** y elige su carpeta `FolderTwin-Batch-…`. La restauración de una demostración anterior usa una carpeta de prueba nueva que se retira al cerrar. CI comprueba ambos recorridos con `-- --smoke`.

En un lote confirmado, pulsa **Verificar respaldo** para comprobar las carpetas y comparar el tamaño y SHA-256 de cada archivo con el catálogo. La ventana señala las rutas ausentes, alteradas o que no pudo leer. La comprobación es de solo lectura sobre el respaldo.

El laboratorio admite el flujo principal de cada archivo. Si encuentra flujos de datos alternativos de NTFS en un archivo o carpeta, lo indica para revisión y conserva el contenido existente.

También revisa los atributos al abrir las rutas, incluida la raíz. Los archivos o carpetas detectados como offline, cifrados con EFS o con puntos de reanálisis quedan para revisión. La validación con proveedores de nube reales sigue pendiente.

El laboratorio también señala los enlaces duros y los atributos extendidos de NTFS que detecta. En archivos comprimidos o dispersos, copia y verifica los bytes de contenido. La vista previa muestra ese tamaño, que puede superar el espacio que ocupa el original en disco.

Los nuevos lotes guardan y restauran las fechas de creación y modificación de los archivos en UTC y su atributo Oculto. Al copiar o restaurar, los archivos y carpetas nuevos heredan los permisos del destino. Las sesiones anteriores se pueden reabrir y restaurar con los metadatos que guardaron. Las fechas de carpetas y los demás atributos siguen fuera del alcance del laboratorio.

Abre **Historial de intentos** para ver las últimas 20 copias, restauraciones o verificaciones de la sesión. En los espacios configurables, cada destino tiene su propio historial fuera del respaldo. Guarda nombres relativos y categorías de incidencia. Si hay registros ilegibles, incidencias que exceden el límite del registro o un fallo de escritura, la ventana lo indica.

Si un archivo está en uso, el laboratorio intenta mostrar las aplicaciones que detecta Windows mediante Restart Manager. Si no puede identificarlas, conserva el aviso y permite reintentar. El diagnóstico no cierra aplicaciones.

Para repetir el ensayo de disco lleno, cambio de letra, destino sustituido y reconexión ordenada, ejecuta **Disposable volume lab** en la pestaña Actions de GitHub. Usa un disco virtual de 128 MiB en un runner Windows desechable. El ensayo de corte eléctrico en hardware queda aplazado. [Alcance y resultados](PLAN.md#laboratorio-de-volumen-desechable).

La revisión de opiniones externas incorpora diagnóstico de archivos en uso, recuperación por carpetas/fecha, bandeja y cuarentena previa al borrado. Las optimizaciones de E/S y consumo se deciden mediante pruebas. El plan distingue medidas de protección de garantías todavía no demostradas.

Este trabajo no sustituye el respaldo existente: no usar prototipos con datos personales.

## Licencia

El proyecto se distribuye bajo [GNU GPL versión 3](LICENSE).
