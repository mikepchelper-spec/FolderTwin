# Recuperar respaldos de las alfas

La ventana de trabajos ofrece **Respaldos de alfa**. Abre las herramientas compatibles en una ventana identificada como compatibilidad. No importa esos almacenes como trabajos de producto ni convierte sus archivos. Conserva el espacio completo, sus marcadores, catálogos, recibos, objetos, diarios y cuarentenas. No cambies a mano sus identidades ni sustituyas archivos de formato.

## Espacios de pruebas y versiones

1. Abre **Respaldos de alfa** y pulsa **Abrir espacio**. Selecciona el espacio original `FolderTwin-Workspace-…` creado por la alfa. Los espacios persistentes están bajo `%LocalAppData%\FolderTwin-Lab\workspaces`; las sesiones temporales usan la carpeta temporal de Windows.
2. Para un lote de copia inicial, pulsa **Abrir trabajo** y selecciona su destino. Un lote confirmado permite **Verificar respaldo** y **Restaurar y verificar**. Elige una subcarpeta nueva y vacía del mismo espacio para recuperar. Su contenido se conserva al cerrar.
3. Para un almacén con fechas, pulsa **Versiones** y **Abrir almacén**. Selecciona la carpeta del almacén, elige la fecha y usa **Verificar punto** antes de restaurar. Puedes restaurar el punto o buscar y recuperar un archivo, incluido un eliminado desde la fecha previa cuando esté disponible. La carpeta de salida debe ser nueva y vacía dentro del espacio.
4. Abre los archivos sintéticos recuperados y compara su contenido. Conserva las incidencias de verificación. Si una fecha está expirada y su cuarentena sigue disponible, revisa y deshaz esa cuarentena antes de recuperarla. No purgues para resolver una incidencia de lectura.

Los lectores de `FTVERS01` y `FTVERS02` continúan disponibles. `FTVERS01` no declara un registro de generaciones suficiente para habilitar retención; no se añade un registro supuesto. `FTVERS02` conserva su protocolo de expectativas replicadas. Una réplica de catálogo dañada puede usar la otra íntegra con aviso; un recibo ausente o catálogos contradictorios requieren revisión.

Los trabajos de producto `FTVERS03` se abren sin registro ni exportación desde **Trabajos → Abrir respaldo existente**. Este lector permite verificar y recuperar en una salida nueva y puede leer un respaldo protegido contra escritura. Conserva sus límites de identidad y confinamiento.

La candidata de correcciones introduce eventos de registro de versión 3 cuando se autoriza purgar metadatos. Lee los registros anteriores; una purga antigua conserva sus catálogos hasta que se autoriza completar esa limpieza. Después de una purga nueva, usa esta candidata o una posterior para escribir y limpiar ese almacén: los paquetes antiguos rechazan los eventos desconocidos. Una fecha definitivamente purgada pierde sus catálogos aunque comparta todos sus contenidos con otra. Conserva el paquete compatible junto con el respaldo y comprueba la recuperación de las fechas retenidas antes de retirar una versión instalada.

## Demostraciones iniciales

Los catálogos v4 de la candidata de correcciones conservan `Zone.Identifier` por ruta. El lector actual admite v1/v2/v3/v4 y no convierte puntos anteriores. Los lectores anteriores rechazan v4; conserva una copia del paquete compatible. Los envíos remotos nuevos usan `ft-v2-job` y `ft-v2-generation`, un catálogo cifrado y `data/`: los paquetes antiguos no enumeran esas fechas. Los puntos remotos con las etiquetas anteriores siguen admitiendo recuperación completa. [Formato y límites](docs/ZONE-IDENTIFIER.md).

Para las demostraciones anteriores `FolderTwin-Batch-…`, usa **Abrir demostración anterior** en la ventana de compatibilidad. El catálogo confirmado habilita **Verificar respaldo** y **Restaurar y verificar** sin el origen. La restauración escribe en una sesión nueva y muestra su ruta completa al terminar. Esa salida recuperada se conserva al cerrar; copia los archivos necesarios a la ubicación elegida antes de retirar manualmente la sesión desechable.

Una demostración pendiente se puede reintentar con su plan original. Cambiar el origen no autoriza completar un contenido distinto del analizado. Los temporales y registros no se deben borrar para forzar el reintento.

## Trabajos de producto e importación

Las candidatas posteriores a `v1.0.0-rc.1` conservan la lectura de los formatos de respaldo, configuración e historial (`v1.0.0-rc.1` está retirada: no uses su escritor para continuar trabajando). La campaña `tests/CompatibilityProbe/Invoke-Compatibility.ps1` permite que un binario anterior cree tres respaldos sintéticos y que el núcleo actual los abra, verifique y restaure sin origen. No equivale a migración de datos personales, prueba en otra cuenta de Windows ni validación de downgrade.

Los respaldos de producto `FTVERS03` se abren mediante su trabajo o una configuración exportada con **Importar configuración**. Revisa destino, marcador e identidad; la importación queda deshabilitada. Recuperar un punto confirmado no exige el origen. Revisa la identidad del origen antes de activar nuevas copias. [Protocolo](docs/CONFIGURACION.md).

El registro de generaciones incorpora un evento nuevo al conservar un plan incompleto y al archivar comprobantes históricos. Los lectores antiguos lo rechazan y no deben volver a escribir en ese almacén. Actualizar la aplicación no convierte ni reescribe los puntos confirmados. [Planes pendientes](docs/PENDIENTES.md).

La actualización que incorpora la vigilancia continua lee los historiales anteriores. Los diarios y resultados de reemplazos acotados añaden un marcador de alcance. Conserva la compilación actual para reanudar o consultar esos registros. Los catálogos de los puntos confirmados conservan su formato y pueden recuperarse con la versión que creó el respaldo aunque el origen ya no esté disponible.

## Evidencia y límites

`--smoke` prueba la reapertura WPF de `FTVERS01` y `FTVERS02`, verificación y restauración con el origen ausente y sin índice previo. También recupera una demostración inicial, cierra la ventana y comprueba que los archivos nuevos permanecen. Mantiene el recorrido de lotes iniciales, reintentos, catálogos dañados y restauración sin origen del espacio configurable. `--product-smoke` comprueba que el panel puede abrir la ventana de compatibilidad y mantiene importación y recuperación de producto.

Los ensayos generan datos sintéticos en los formatos admitidos. No acreditan cada compilación histórica ni el acceso desde otra cuenta o equipo. Los lectores de laboratorio exigen el espacio original y su ubicación autorizada; no admiten adoptar una copia movida a una ruta cualquiera. Si cambió esa ubicación o falta un marcador, conserva el original, la compilación que lo creó y la incidencia para revisión. No deshabilites las comprobaciones de identidad. Las comprobaciones externas permanecen en [V1.md](V1.md).
