# Recuperar respaldos de las alfas

La ventana de trabajos ofrece **Respaldos de alfa**. Abre las herramientas compatibles en una ventana identificada como compatibilidad. No importa esos almacenes como trabajos de producto ni convierte sus archivos. Conserva el espacio completo, sus marcadores, catálogos, recibos, objetos, diarios y cuarentenas. No cambies a mano sus identidades ni sustituyas archivos de formato.

## Espacios de pruebas y versiones

1. Abre **Respaldos de alfa** y pulsa **Abrir espacio**. Selecciona el espacio original `FolderTwin-Workspace-…` creado por la alfa. Los espacios persistentes están bajo `%LocalAppData%\FolderTwin-Lab\workspaces`; las sesiones temporales usan la carpeta temporal de Windows.
2. Para un lote de copia inicial, pulsa **Abrir trabajo** y selecciona su destino. Un lote confirmado permite **Verificar respaldo** y **Restaurar y verificar**. Elige una subcarpeta nueva y vacía del mismo espacio para recuperar. Su contenido se conserva al cerrar.
3. Para un almacén con fechas, pulsa **Versiones** y **Abrir almacén**. Selecciona la carpeta del almacén, elige la fecha y usa **Verificar punto** antes de restaurar. Puedes restaurar el punto o buscar y recuperar un archivo, incluido un eliminado desde la fecha previa cuando esté disponible. La carpeta de salida debe ser nueva y vacía dentro del espacio.
4. Abre los archivos sintéticos recuperados y compara su contenido. Conserva las incidencias de verificación. Si una fecha está expirada y su cuarentena sigue disponible, revisa y deshaz esa cuarentena antes de recuperarla. No purgues para resolver una incidencia de lectura.

Los lectores de `FTVERS01` y `FTVERS02` continúan disponibles. `FTVERS01` no declara un registro de generaciones suficiente para habilitar retención; no se añade un registro supuesto. `FTVERS02` conserva su protocolo de expectativas replicadas. Una réplica de catálogo dañada puede usar la otra íntegra con aviso; un recibo ausente o catálogos contradictorios requieren revisión.

## Demostraciones iniciales

Para las demostraciones anteriores `FolderTwin-Batch-…`, usa **Abrir demostración anterior** en la ventana de compatibilidad. El catálogo confirmado habilita **Verificar respaldo** y **Restaurar y verificar** sin el origen. La restauración escribe en una sesión nueva y muestra su ruta completa al terminar. Esa salida recuperada se conserva al cerrar; copia los archivos necesarios a la ubicación elegida antes de retirar manualmente la sesión desechable.

Una demostración pendiente se puede reintentar con su plan original. Cambiar el origen no autoriza completar un contenido distinto del analizado. Los temporales y registros no se deben borrar para forzar el reintento.

## Trabajos de producto e importación

Los respaldos de producto `FTVERS03` se abren mediante su trabajo o una configuración exportada con **Importar configuración**. Revisa destino, marcador e identidad; la importación queda deshabilitada. Recuperar un punto confirmado no exige el origen. Revisa la identidad del origen antes de activar nuevas copias. [Protocolo](experiments/CopyProbe/CONFIGURACION.md).

El registro de generaciones incorpora un evento nuevo al conservar un plan incompleto. Los lectores antiguos lo rechazan y no deben volver a escribir en ese almacén. Actualizar la aplicación no convierte ni reescribe los puntos confirmados. [Planes pendientes](experiments/CopyProbe/PENDIENTES.md).

## Evidencia y límites

`--smoke` prueba la reapertura WPF de `FTVERS01` y `FTVERS02`, verificación y restauración con el origen ausente y sin índice previo. También recupera una demostración inicial, cierra la ventana y comprueba que los archivos nuevos permanecen. Mantiene el recorrido de lotes iniciales, reintentos, catálogos dañados y restauración sin origen del espacio configurable. `--product-smoke` comprueba que el panel puede abrir la ventana de compatibilidad y mantiene importación y recuperación de producto.

Los ensayos generan datos sintéticos en los formatos admitidos. No acreditan cada compilación histórica ni el acceso desde otra cuenta o equipo. Los lectores de laboratorio exigen el espacio original y su ubicación autorizada; no admiten adoptar una copia movida a una ruta cualquiera. Si cambió esa ubicación o falta un marcador, conserva el original, la compilación que lo creó y la incidencia para revisión. No deshabilites las comprobaciones de identidad. La aceptación se repetirá desde el paquete final, y las comprobaciones externas permanecen en [V1.md](V1.md).
