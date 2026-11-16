# Guía de uso de FolderTwin

Esta distribución es una vista previa de la v1 para Windows 11 x64. Usa únicamente archivos sintéticos y carpetas desechables. El paquete incluye .NET y se usa sin consola. Consulta [Instalación y actualización](INSTALACION.md) para preparar el ejecutor o retirar la aplicación.

## Vigilancia en la candidata en desarrollo

En una compilación que incluya esta función, el asistente recomienda **Automático al detectar cambios** para trabajos nuevos. Puedes elegir también una hora diaria. Al guardar el trabajo automático, FolderTwin solicita iniciar el monitor y preparar la primera copia. En **Espejo protegido**, puedes permitir por separado las retiradas ordinarias recuperables. El origen vacío, los cambios de gran alcance y los conflictos requieren revisión.

En Ruta, consulta el estado del monitor, la última comparación automática, el último respaldo completo y la última verificación. **Ediciones comprobadas** indica que se guardaron y verificaron los archivos observados. La comparación completa se registra por separado. Si falta el monitor, abre **Editar trabajo y automatización → Iniciar monitor**. Desde los mismos ajustes puedes pausar o reanudar la vigilancia de ese trabajo o desmarcarla y guardar. La pausa de vigilancia se aplica a los próximos intentos automáticos. Un intento en marcha tiene sus propios controles.

Si un archivo sigue ocupado por un escritor, FolderTwin puede guardar los archivos legibles en un **Punto parcial**. El trabajo sigue pendiente hasta que termine una copia completa. Puedes verificar y restaurar ese punto desde **Recuperar**, incluso con el origen desconectado. La carpeta actual del espejo o de la copia acumulativa permanece en su estado anterior durante ese intento.

La vigilancia puede continuar al cerrar la ventana con Windows despierto y tu sesión iniciada. En **Aplicación**, usa **Iniciar la vigilancia al entrar en Windows** y **Guardar e iniciar monitor** para configurar el arranque. **Detener vigilancia** retira la tarea del monitor y conserva los horarios diarios y los respaldos. La vigilancia pertenece a esta candidata local. [Paquete experimental público RC2](https://github.com/mikepchelper-spec/FolderTwin/releases/tag/v1.0.0-rc.2).

## Primer inicio

Descarga el artefacto del commit elegido desde [GitHub Actions](https://github.com/mikepchelper-spec/FolderTwin/actions/workflows/copy-probe.yml), extrae **todo** su contenido en una carpeta nueva y abre **FolderTwin.exe**. Conserva los archivos que lo acompañan. La pantalla **Trabajos** estará vacía si esa cuenta todavía no tiene configuración de producto. Las herramientas de **Respaldos de alfa** son para formatos anteriores; no hacen falta para crear trabajos nuevos.

Prepara una carpeta de origen con datos de prueba y otra ubicación local NTFS donde guardar los respaldos. Cada trabajo crea su propia subcarpeta dedicada en el destino. Evita rutas anidadas, enlaces y carpetas que otro programa use como respaldo. Las copias en el mismo disco físico sirven para ensayar, pero no protegen frente al fallo de ese disco.

Si todavía no hay trabajos, **Crear mi primer respaldo** abre el asistente. Guardar crea la configuración y la carpeta dedicada. Un trabajo automático prepara su primera copia. Para probar de inmediato un trabajo manual o con horario, pulsa **Respaldar ahora**. Al terminar, el trabajo indica cómo verificar el punto y recuperar una muestra.

En **Aplicación**, **Importar un trabajo** y **Recuperar archivos**, usa **Volver a trabajos** en la cabecera para regresar a la lista conservando tu selección. En el asistente, **Atrás** vuelve al paso anterior y **Cancelar** regresa a la lista.

## Tercera copia en nube

En el paquete de nube, selecciona un trabajo sintético en Ruta y abre **Copia en nube**. Elige el proveedor y un bucket privado o una carpeta remota dedicada. Usa **Crear y activar copia** para un repositorio nuevo o **Conectar y activar copia** para uno que ya exista. Guarda la contraseña del repositorio fuera del equipo. Si la instalación tiene un ejecutor de una candidata anterior, usa primero **Aplicación → Instalar o actualizar ejecutor**.

FolderTwin envía puntos locales completos. **Actualizar envíos** muestra la última confirmación remota, los envíos pendientes y la última restauración verificada. Puedes pausar toda la copia remota del trabajo o controlar un envío individual. Ante un problema de acceso, vuelve a conectar con una credencial válida. Si falta el disco local, reconéctalo. Si se agotó la cuota, revisa el espacio del proveedor y reintenta.

Al crear un repositorio, repite su contraseña de recuperación y confirma que has guardado su ubicación y contraseña fuera del equipo. FolderTwin comprueba una nueva apertura de lectura antes de activar el destino. Elige el envío en **Envío que quieres controlar** antes de pausarlo, cancelarlo o reintentarlo; las páginas permiten consultar los anteriores. Ruta y bandeja distinguen el estado local del remoto, incluida la fecha confirmada y el atraso. Un error de nube conserva su incidencia y permite continuar las copias locales.

Para recuperar en otro equipo, abre **Copia en nube** aunque la lista de trabajos esté vacía. Introduce la ubicación y las credenciales del repositorio, pulsa **Consultar fechas de nube**, elige una fecha y usa **Recuperar en carpeta nueva**. Los perfiles de Filen y MEGA siguen en validación externa. Usa carpetas sintéticas hasta completar la prueba con tu proveedor.

## Ayuda y versión

**ⓘ Ayuda y versión**, arriba de la ventana, o **F1** abre las explicaciones de los controles visibles. Puedes expandir cada explicación con un clic o con el teclado. Al pasar el mouse sobre un control, aparece también su ayuda breve. Si está deshabilitado, la explicación indica qué falta para usarlo. Para consultar una sección plegada, ábrela antes de entrar en Ayuda.

La ventana de ayuda incluye esta guía, soporte y el commit exacto del paquete. En **Diagnóstico**, pulsa **Preparar resumen**, revisa el texto y usa **Copiar resumen revisado**. El resumen contiene versión, entorno y recuentos de estado. Describe aparte el problema con datos inventados y revisa cualquier texto antes de enviarlo. El enlace de soporte abre el formulario público de GitHub.

## Crear un trabajo

1. Pulsa **Nuevo trabajo** y **Elegir origen**. Selecciona la carpeta con los archivos y pulsa **Siguiente**.
2. Pulsa **Elegir destino**, selecciona la ubicación donde se creará la carpeta dedicada y continúa.
3. Escribe un nombre y elige el modo. Selecciona **Automático al detectar cambios** para vigilar el origen. Puedes añadir un horario diario en hora local, por ejemplo `20:30`. Si dejas ambas opciones desactivadas, el trabajo se ejecuta a demanda.
4. Revisa origen, destino nuevo, modo, disparadores y espacio observado. **Atrás** permite corregirlos. Pulsa **Guardar trabajo** y comprueba el estado en Ruta.

| Modo | Qué conserva y qué revisar |
|---|---|
| Copia acumulativa | Agrega y actualiza archivos y conserva los exclusivos del destino. Detiene los conflictos con ediciones externas; no los sobrescribe para terminar. Usa versionado si necesitas elegir fechas como función principal. |
| Versionado | Guarda puntos por fecha y permite recuperar archivos que después se modificaron o desaparecieron del origen. Los contenidos sin cambios se comparten internamente. Usa la recuperación de la aplicación para extraerlos. |
| Espejo protegido | Mantiene una carpeta actual y conserva el contenido anterior. Si autorizas las retiradas ordinarias automáticas, los archivos retirados se pueden recuperar desde el punto anterior. El origen vacío, los conflictos y los cambios masivos requieren revisión. |

El horario funciona con tu sesión iniciada. Si falla su preparación, revisa el aviso y usa **Editar trabajo y automatización → Reparar horario**. El trabajo queda guardado y Ruta muestra si el horario está pendiente.

## Revisar el estado y ejecutar

En **Ruta de respaldo**, selecciona un trabajo para ver las tarjetas **Origen** y **Destino**, su estado y las fechas de respaldo y verificación. La flecha indica la dirección de la copia. Si el volumen tiene una letra visible en Windows, la tarjeta la muestra y abrevia las rutas largas. Pasa el mouse sobre la ruta para leerla completa o abre **Carpetas del trabajo** para consultar las rutas registradas.

Selecciona un trabajo para consultar el último respaldo completo, la última verificación explícita, el punto recuperable, el horario o próximo reintento y el espacio observado. Un intento fallido y una restauración no cambian la fecha del último respaldo completo. **Historial e incidencias** conserva los resultados y la ruta afectada cuando está disponible.

El historial muestra treinta intentos por página. **Anteriores** y **Más recientes** permiten recorrerlo; **Mostrar solo incidencias** busca en todo el historial. Consultar una página anterior conserva la selección mientras llegan nuevos intentos.

**Respaldar ahora** inicia un trabajador independiente. El pie muestra la fase, el archivo y el progreso. **Pausar**, continuar y **Cancelar ejecución** se atienden en límites seguros; un paso ya iniciado puede terminar. Cancelar conserva los datos parciales para revisión y reintento. Cerrar la ventana deja continuar al trabajador; al abrir de nuevo se consulta su estado persistido.

En el espejo, **Respaldar ahora** prepara una revisión. Comprueba altas, modificaciones, retiradas, conflictos y espacio antes de **Aplicar espejo revisado**. Cuando aparezcan, revisa y acepta por separado el origen vacío y el alcance de los cambios. Si ya existe una actualización autorizada pendiente, **Reanudar actualización** continúa su diario; no empieza otra con datos diferentes.

**Minimizar a bandeja** deja la interfaz accesible desde el área de notificaciones. Su panel muestra la salud de la vigilancia, la antigüedad del respaldo y las incidencias. Desde ahí puedes pulsar **Respaldar ahora** o **Abrir aplicación**. El monitor residente y los horarios siguen su propia configuración aunque cierres la interfaz.

## Verificar y recuperar

1. Selecciona el trabajo y pulsa **Recuperar archivos**. Elige una **Fecha del respaldo** disponible.
2. Usa **Verificar respaldo** para comprobar su contenido y espera el resultado. La verificación del estado actual de acumulativo o espejo también comprueba su carpeta actual cuando corresponde.
3. Navega con **Abrir carpeta** y **Carpeta superior**, o escribe un nombre o ruta y pulsa **Buscar**. La búsqueda abarca el punto elegido. **Mostrar solo cambios respecto al punto anterior** ayuda a encontrar archivos modificados o eliminados; usa las páginas cuando haya muchos resultados.
4. Pulsa **Recuperar punto completo**, o selecciona un archivo y **Recuperar archivo elegido**. Un archivo marcado como eliminado se toma del punto anterior que lo conserva.
5. Elige dónde crear la salida. Revisa la fecha, el alcance y la carpeta nueva y pulsa **Recuperar en carpeta nueva**. Espera el resultado y abre los archivos restaurados para comprobarlos.

Después de una recuperación confirmada, **Abrir carpeta recuperada** muestra su salida en el Explorador. El botón está disponible tanto en Recuperar como en los detalles del trabajo. Se vuelve a comprobar la identidad de la carpeta padre antes de abrirla.

La salida debe estar separada de los orígenes, destinos y configuración de los trabajos. La aplicación crea una subcarpeta nueva y evita reemplazar una restauración anterior. Si se interrumpe una recuperación, conserva la salida parcial; para repetir, revisa otra carpeta nueva. Recuperar un punto confirmado no necesita que el origen siga conectado. Una fecha expirada requiere deshacer su cuarentena cuando todavía sea reversible.

## Resolver una incidencia

Si perdiste el registro de trabajos o la exportación, vuelve a **Trabajos → Abrir respaldo existente** y elige la subcarpeta dedicada del respaldo. El lector admite el formato de producto FTVERS03: ofrece fechas, búsqueda, carpetas, verificación y recuperación completa o de un archivo en una carpeta nueva. No habilita copias ni retención en ese respaldo. Puedes recuperar con el origen ausente y con el respaldo protegido contra escritura. Los formatos de alfa conservan su procedimiento en [Compatibilidad](COMPATIBILIDAD.md).

Si hay una configuración dañada cuyas rutas no pueden comprobarse, elige como ubicación de salida la raíz de un disco, por ejemplo `D:\`. La aplicación crea una subcarpeta nueva y conserva el registro dañado para investigar. Espera a que terminen los trabajadores activos antes de recuperar. Una cancelación conserva la salida parcial identificada en pantalla; para repetir usa otra carpeta nueva.

| Aviso o situación | Acción |
|---|---|
| Archivo o destino en uso | Guarda y cierra la aplicación que lo usa o espera a que termine la otra operación. Consulta la ruta indicada y pulsa **Reintentar respaldo**. Para un intento de verificación o recuperación, vuelve a la acción correspondiente. |
| Origen o destino ausente | Reconecta la misma raíz registrada y reintenta. Un disco diferente con la misma letra no sustituye su identidad. |
| Espacio insuficiente | Selecciona el trabajo indicado en Ruta o abre el aviso de bandeja. Libera espacio en su destino fuera de los archivos internos del respaldo y pulsa **Reintentar respaldo**. La fecha del último respaldo completo permanece visible. La cuarentena todavía ocupa espacio hasta que se autoriza su purga. |
| Edición externa en destino | Conserva aparte la edición y revisa la ruta indicada. No fuerces la sustitución ni borres metadatos de FolderTwin. Repite cuando el estado requerido vuelva a coincidir. |
| Plan anterior incompatible con el origen | En versionado, **Resolver un plan de versionado pendiente** permite revisar y conservar un plan completo antes de iniciar otro en el mismo almacén. Para borradores u otros modos, espera el fin del trabajador, retira el trabajo desde su configuración y crea otro con una carpeta dedicada nueva. Conserva el anterior para recuperar; sus datos parciales y diario siguen ocupando espacio. No se descartan ni se completan con el origen cambiado. [Procedimiento y límites](experiments/CopyProbe/PENDIENTES.md). |
| Horario que requiere atención | Revisa la hora y el trabajo habilitado y usa **Reparar horario**. Un destino ausente o un almacén ocupado admite hasta tres reintentos programados, separados por cinco minutos. Deshabilitar el trabajo detiene la espera. |
| Catálogo dañado o contradictorio | Conserva el respaldo y el mensaje. Una réplica íntegra puede permitir recuperar con aviso; referencias dudosas detienen la limpieza. No edites sus archivos para eludir la comprobación. |

El historial puede mantener la incidencia anterior después de un intento correcto. Eso permite revisar lo ocurrido; no equivale a que el último intento haya fallado. Si ves contenido inesperado, una retirada sin revisión o un punto incompleto anunciado como correcto, deshabilita el trabajo y conserva todo para investigar.

## Editar, exportar y retirar trabajos

En **Editar trabajo y automatización**, cambia nombre, hora o **Trabajo habilitado** y pulsa **Guardar cambios**. **Retirar trabajo; conservar datos y recuperación** lo desactiva y mantiene sus respaldos accesibles. Para reactivarlo, desmarca la retirada y vuelve a habilitarlo. Cambiar carpetas o modo exige crear otro trabajo.

Dentro de **Editar trabajo y automatización**, **Exportar configuración** guarda opciones, rutas e identidades, sin credenciales ni historial. Revisa las rutas antes de compartir el archivo. **Importar configuración** exige elegir el respaldo existente y comprobar sus identidades. Puedes elegir el origen para futuras copias o **Recuperar sin origen**. Acepta la revisión y **Guardar trabajo deshabilitado**. Para activarlo después, revisa el origen importado y habilítalo expresamente.

## Retención manual

Abre **Retención manual y cuarentena**, revisa días, semanas, meses y gracia y pulsa **Revisar retención**. Las fechas se seleccionan con calendario UTC. La revisión distingue los bytes de objetos que irán a cuarentena y los de catálogos y descriptores que retiraría la purga posterior. Se conservan comprobantes; el espacio neto liberado puede ser menor. Comprueba qué expirará y acepta la revisión antes de **Mover a cuarentena**. Esta operación conserva los objetos durante la gracia y no libera todavía el espacio que ocupan.

El historial de cuarentenas permite **Reanudar movimientos** o **Deshacer cuarentena**. La purga es una acción posterior: espera la gracia desde la finalización, vuelve a verificar un punto retenido y acepta el borrado irreversible antes de **Purgar tras el plazo de gracia**. Retira los objetos exclusivos y los metadatos completos de las fechas expiradas, que dejan de aparecer en recuperación. Conserva comprobantes. Si se interrumpe, aparece **purga pendiente**: reanuda con el mismo botón. Una purga antigua puede indicar **metadatos conservados** y ofrecer completar explícitamente esa parte. Catálogos ausentes o contradictorios y operaciones pendientes suspenden la limpieza. No se ejecuta retención automática.

## Actualizar o desinstalar

Abre **Aplicación** para revisar los trabajos e **Instalar o actualizar ejecutor**. Al actualizar, se conservan las versiones anteriores y se redirigen solo los horarios propios habilitados. Espera a que terminen trabajadores y reintentos activos. Para retirar, quita primero los horarios, deshabilita los trabajos y revisa cada versión instalada. La versión abierta se retira desde otra copia portable. Se conservan los respaldos, configuración y resultados. [Pasos y límites de desinstalación](INSTALACION.md).

Los formatos anteriores se abren desde **Aplicación → Respaldos de alfa**, según [COMPATIBILIDAD.md](COMPATIBILIDAD.md). Conserva el paquete anterior hasta comprobar la reapertura y una restauración de muestra después de actualizar.

## Alcance y comprobaciones pendientes

El alcance es carpetas y discos locales NTFS en Windows 11 x64. VSS, las instantáneas coherentes de bases de datos abiertas, NAS, la nube propia y la sincronización bidireccional quedan fuera. Los enlaces, EFS, flujos no admitidos y archivos de nube que todavía necesitan descargarse pueden generar incidencias. El desarrollo conserva la marca de procedencia `Zone.Identifier`, hasta 16 KiB, como metadato de cada archivo; conserva tanto su ausencia como una marca vacía. Los otros flujos alternativos requieren revisión. Se respaldan el contenido y los metadatos admitidos de los archivos.

La matriz `V1.md` en las fuentes distingue las pruebas de software, las observaciones de escritorio y las validaciones externas. El [procedimiento del piloto](PILOTO.md) describe cómo registrar 7–14 días de ensayos de entorno con datos sintéticos.
