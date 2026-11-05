# Configuración portable de trabajos

La vista previa de producto permite exportar un trabajo e importar su configuración desde la ventana principal. Los ensayos usan únicamente carpetas desechables con datos sintéticos. El archivo contiene nombre, modo, hora, rutas e identidades. Revisa ese contenido antes de compartirlo. El esquema no incluye credenciales, índices, inventarios de archivos ni historial de ejecuciones.

## Recorrido en la interfaz

1. Selecciona un trabajo y pulsa **Exportar configuración**. Elige un archivo nuevo con extensión `.foldertwin.json`, fuera de los orígenes, respaldos y configuración local. Un archivo existente se conserva.
2. En la otra configuración o cuenta, pulsa **Importar configuración** y abre el archivo. Elige la carpeta dedicada del respaldo existente. No elijas su carpeta padre ni otro disco con una carpeta del mismo nombre.
3. Puedes elegir también el origen original. Si está ausente, usa **Recuperar sin origen**. Pulsa **Comprobar identidades**, revisa modo y carpetas y acepta la revisión antes de guardar.
4. El trabajo queda deshabilitado. Puedes abrir **Recuperar archivos**, elegir una fecha y recuperar en una carpeta nueva. La importación conserva los puntos del respaldo y crea una identidad local nueva para el trabajo; no registra horarios.
5. Para nuevas copias, abre **Editar trabajo y horario**. Si el origen no se revisó, pulsa **Revisar origen importado** y elígelo. Debe conservar la misma identidad física, aunque haya cambiado de ruta. Marca **Trabajo habilitado** y guarda los ajustes. Habilitar vuelve a comprobar origen, destino y marcador. El horario se instala solo tras esa activación explícita.

Una carpeta diferente con los mismos archivos no sustituye al origen registrado. Crea otro trabajo para un origen distinto. Un respaldo ya registrado en la misma configuración tampoco se vuelve a adoptar. Conserva el trabajo existente y edítalo o recupera desde él. Para otra cuenta o equipo se necesitan permisos de acceso y la validación externa correspondiente; la prueba local con dos registros no la sustituye.

## Formato y límites

El archivo UTF-8 JSON usa el identificador `FolderTwin job`, versión de esquema, trabajo original, opciones, identidad de origen, marcador e identidad de destino y rutas conocidas. Un SHA-256 detecta corrupción accidental del contenido. No autentica a su autor: la revisión física de las carpetas sigue siendo obligatoria. La lectura rechaza propiedades desconocidas o repetidas, campos inválidos y archivos mayores de 64 KiB. Las exportaciones se excluyen de Git mediante su extensión.

La importación guarda una intención ligada al nuevo registro local y al destino existente. Publica destino y confirmación sin reemplazo y puede reanudarse desde los mismos límites del registro normal. Comprueba el marcador existente; no instala otro ni modifica los catálogos para importar. Solo admite el almacén de producto `FTVERS03`, o la carpeta dedicada todavía vacía que conserva su marcador. Un almacén con preparación incompleta o contenido desconocido queda para revisión. Los formatos de laboratorio conservan sus lectores; **Respaldos de alfa** abre sus herramientas desde el panel de producto. El [procedimiento de recuperación](../../COMPATIBILIDAD.md) y sus pruebas no convierten esos almacenes en trabajos nuevos.

## Cambio de ruta del mismo origen

El origen elegido debe conservar identificador de archivo y de volumen. La revisión conserva las rutas anteriores en una lista acotada y solo agrega rutas después de comprobar la identidad. El motor admite esas rutas al leer descriptores antiguos, manteniendo sus hashes, identidad de destino e identidad de origen. Las lecturas nuevas usan la carpeta actual comprobada; no siguen enlaces ni vuelven a buscar el origen por su nombre. La retención sigue vinculando cada catálogo a su origen registrado.

Las revisiones de origen son registros inmutables encadenados en la configuración del trabajo. No reescriben la intención original ni invalidan resultados anteriores. Exigen el trabajo deshabilitado y ausencia de trabajadores activos o en espera. La creación de otros trabajos, recuperación y exportación usan la raíz efectiva revisada al comprobar solapamientos.

## Evidencia

`--product-config` prueba los tres modos, exportación sin sobrescritura ni escritura en el origen, checksum, revisión sin registro, importación deshabilitada, conservación de fechas, recuperación con origen ausente, rechazo de otra identidad, origen y respaldo renombrados, revisión posterior del origen, activación, reexportación y referencias de retención. Interrumpe y reanuda cada límite de registro. También importa versiones, acumulativos y espejos pendientes después de renombrar el origen y completa la misma generación sin reescribir sus descriptores.

`--product-smoke` recorre exportación e importación de los tres modos desde WPF, recupera sus fechas originales, rechaza el origen sustituido y comprueba que la programación solo se solicita tras la activación explícita. La programación nativa se comprueba por separado. Ambas pruebas usan fixtures locales y no dan por validados permisos, ACL, montaje de volúmenes ni interfaz en otra cuenta o equipo.
