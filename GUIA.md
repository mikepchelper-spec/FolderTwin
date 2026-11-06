# Guía de uso de FolderTwin

La distribución actual es una vista previa de la v1 para Windows 11 x64. Usa únicamente archivos sintéticos y carpetas desechables mientras siga abierta la [matriz de aceptación](V1.md). El paquete incluye .NET y se usa sin consola. Consulta [Instalación y actualización](INSTALACION.md) para preparar el ejecutor o retirar la aplicación.

## Primer inicio

Descarga el artefacto del commit elegido desde [GitHub Actions](https://github.com/mikepchelper-spec/FolderTwin/actions/workflows/copy-probe.yml), extrae **todo** su contenido en una carpeta nueva y abre **FolderTwin.exe**. Conserva los archivos que lo acompañan. La pantalla **Trabajos** estará vacía si esa cuenta todavía no tiene configuración de producto. Las herramientas de **Respaldos de alfa** son para formatos anteriores; no hacen falta para crear trabajos nuevos.

Prepara una carpeta de origen con datos de prueba y otra ubicación local NTFS donde guardar los respaldos. Cada trabajo crea su propia subcarpeta dedicada en el destino. Evita rutas anidadas, enlaces y carpetas que otro programa use como respaldo. Las copias en el mismo disco físico sirven para ensayar, pero no protegen frente al fallo de ese disco.

Si todavía no hay trabajos, **Crear mi primer respaldo** abre el asistente. Guardar crea la configuración y la carpeta dedicada. Después pulsa **Respaldar ahora**. Al terminar, el trabajo indica cómo verificar el punto y recuperar una muestra.

En **Aplicación**, **Importar un trabajo** y **Recuperar archivos**, usa **Volver a trabajos** en la cabecera para regresar a la lista conservando tu selección. En el asistente, **Atrás** vuelve al paso anterior y **Cancelar** regresa a la lista.

## Ayuda y versión

**ⓘ Ayuda y versión**, arriba de la ventana, o **F1** abre las explicaciones de los controles visibles. Puedes expandir cada explicación con un clic o con el teclado. Al pasar el mouse sobre un control, aparece también su ayuda breve. Si está deshabilitado, la explicación indica qué falta para usarlo. Para consultar una sección plegada, ábrela antes de entrar en Ayuda.

La ventana de ayuda incluye esta guía, soporte y el commit exacto del paquete. En **Diagnóstico**, pulsa **Preparar resumen**, revisa el texto y usa **Copiar resumen revisado**. El resumen contiene versión, entorno y recuentos de estado. Describe aparte el problema con datos inventados y revisa cualquier texto antes de enviarlo. El enlace de soporte abre el formulario público de GitHub.

## Crear un trabajo

1. Pulsa **Nuevo trabajo** y **Elegir origen**. Selecciona la carpeta con los archivos y pulsa **Siguiente**.
2. Pulsa **Elegir destino**, selecciona la ubicación donde se creará la carpeta dedicada y continúa.
3. Escribe un nombre, elige el modo y, si quieres un horario diario, introduce una hora local como `20:30`. Dejarla vacía permite ejecutar a demanda.
4. Revisa origen, destino nuevo, modo, horario y espacio observado. **Atrás** permite corregirlos. **Guardar trabajo** registra la configuración; después usa **Respaldar ahora** para hacer la primera copia.

| Modo | Qué conserva y qué revisar |
|---|---|
| Copia acumulativa | Agrega y actualiza archivos y conserva los exclusivos del destino. Detiene los conflictos con ediciones externas; no los sobrescribe para terminar. Usa versionado si necesitas elegir fechas como función principal. |
| Versionado | Guarda puntos por fecha y permite recuperar archivos que después se modificaron o desaparecieron del origen. Los contenidos sin cambios se comparten internamente. Usa la recuperación de la aplicación para extraerlos. |
| Espejo protegido | Mantiene una carpeta actual y conserva el contenido anterior. Las retiradas, el origen vacío y los cambios masivos requieren revisión antes de aplicarlos. |

El horario funciona con tu sesión iniciada. Si su preparación falla después de guardar, el trabajo permanece registrado: revisa el aviso y usa **Editar trabajo y horario → Reparar horario**. No se anuncia un horario correcto solo por haber guardado la hora.

## Revisar el estado y ejecutar

Selecciona un trabajo para consultar el último respaldo completo, la última verificación explícita, el punto recuperable, el horario o próximo reintento y el espacio observado. Un intento fallido y una restauración no cambian la fecha del último respaldo completo. **Historial e incidencias** conserva los resultados y la ruta afectada cuando está disponible.

**Respaldar ahora** inicia un trabajador independiente. El pie muestra la fase, el archivo y el progreso. **Pausar**, continuar y **Cancelar ejecución** se atienden en límites seguros; un paso ya iniciado puede terminar. Cancelar conserva los datos parciales para revisión y reintento. Cerrar la ventana deja continuar al trabajador; al abrir de nuevo se consulta su estado persistido.

En el espejo, **Respaldar ahora** prepara una revisión. Comprueba altas, modificaciones, retiradas, conflictos y espacio antes de **Aplicar espejo revisado**. Cuando aparezcan, revisa y acepta por separado el origen vacío y el alcance de los cambios. Si ya existe una actualización autorizada pendiente, **Reanudar actualización** continúa su diario; no empieza otra con datos diferentes.

**Minimizar a bandeja** mantiene el monitor opcional. Su panel muestra el estado conjunto, antigüedad e incidencias y permite **Respaldar ahora** o **Abrir aplicación**. Cerrar la interfaz y mantenerla en bandeja son acciones distintas. El monitor no sustituye al trabajador ni al horario.

## Verificar y recuperar

1. Selecciona el trabajo y pulsa **Recuperar archivos**. Elige una **Fecha del respaldo** disponible.
2. Usa **Verificar respaldo** para comprobar su contenido y espera el resultado. La verificación del estado actual de acumulativo o espejo también comprueba su carpeta actual cuando corresponde.
3. Navega con **Abrir carpeta** y **Carpeta superior**, o escribe un nombre o ruta y pulsa **Buscar**. La búsqueda abarca el punto elegido. **Mostrar solo cambios respecto al punto anterior** ayuda a encontrar archivos modificados o eliminados; usa las páginas cuando haya muchos resultados.
4. Pulsa **Recuperar punto completo**, o selecciona un archivo y **Recuperar archivo elegido**. Un archivo marcado como eliminado se toma del punto anterior que lo conserva.
5. Elige dónde crear la salida. Revisa la fecha, el alcance y la carpeta nueva y pulsa **Recuperar en carpeta nueva**. Espera el resultado y abre los archivos restaurados para comprobarlos.

Después de una recuperación confirmada, **Abrir carpeta recuperada** muestra su salida en el Explorador. El botón está disponible tanto en Recuperar como en los detalles del trabajo. Se vuelve a comprobar la identidad de la carpeta padre antes de abrirla.

La salida debe estar separada de los orígenes, destinos y configuración de los trabajos. La aplicación crea una subcarpeta nueva y evita reemplazar una restauración anterior. Si se interrumpe una recuperación, conserva la salida parcial; para repetir, revisa otra carpeta nueva. Recuperar un punto confirmado no necesita que el origen siga conectado. Una fecha expirada requiere deshacer su cuarentena cuando todavía sea reversible.

## Resolver una incidencia

| Aviso o situación | Acción |
|---|---|
| Archivo o destino en uso | Guarda y cierra la aplicación que lo usa o espera a que termine la otra operación. Consulta la ruta indicada y pulsa **Reintentar respaldo**. Para un intento de verificación o recuperación, vuelve a la acción correspondiente. |
| Origen o destino ausente | Reconecta la misma raíz registrada y reintenta. Un disco diferente con la misma letra no sustituye su identidad. |
| Espacio insuficiente | Revisa la capacidad sin eliminar archivos internos del respaldo. La cuarentena todavía ocupa espacio. Un resultado parcial no cuenta como respaldo completo. |
| Edición externa en destino | Conserva aparte la edición y revisa la ruta indicada. No fuerces la sustitución ni borres metadatos de FolderTwin. Repite cuando el estado requerido vuelva a coincidir. |
| Plan anterior incompatible con el origen | En versionado, **Resolver un plan de versionado pendiente** permite revisar y conservar un plan completo antes de iniciar otro en el mismo almacén. Para borradores u otros modos, espera el fin del trabajador, retira el trabajo desde su configuración y crea otro con una carpeta dedicada nueva. Conserva el anterior para recuperar; sus datos parciales y diario siguen ocupando espacio. No se descartan ni se completan con el origen cambiado. [Procedimiento y límites](experiments/CopyProbe/PENDIENTES.md). |
| Horario que requiere atención | Revisa la hora y el trabajo habilitado y usa **Reparar horario**. Un destino ausente o un almacén ocupado admite hasta tres reintentos programados, separados por cinco minutos. Deshabilitar el trabajo detiene la espera. |
| Catálogo dañado o contradictorio | Conserva el respaldo y el mensaje. Una réplica íntegra puede permitir recuperar con aviso; referencias dudosas detienen la limpieza. No edites sus archivos para eludir la comprobación. |

El historial puede mantener la incidencia anterior después de un intento correcto. Eso permite revisar lo ocurrido; no equivale a que el último intento haya fallado. Si ves contenido inesperado, una retirada sin revisión o un punto incompleto anunciado como correcto, deshabilita el trabajo y conserva todo para investigar.

## Editar, exportar y retirar trabajos

En **Editar trabajo y horario**, cambia nombre, hora o **Trabajo habilitado** y pulsa **Guardar cambios**. **Retirar trabajo; conservar datos y recuperación** lo desactiva y mantiene sus respaldos accesibles. Para reactivarlo, desmarca la retirada y vuelve a habilitarlo. Cambiar carpetas o modo exige crear otro trabajo.

Dentro de **Editar trabajo y horario**, **Exportar configuración** guarda opciones, rutas e identidades, sin credenciales ni historial. Las rutas pueden revelar información personal y se deben revisar antes de compartir el archivo. **Importar configuración** exige elegir el respaldo existente y comprobar sus identidades. Puedes elegir el origen para futuras copias o **Recuperar sin origen**. Acepta la revisión y **Guardar trabajo deshabilitado**. Para activarlo después, revisa el origen importado y habilítalo expresamente. La importación no reasigna un respaldo ajeno por tener el mismo nombre.

## Retención manual

Abre **Retención manual y cuarentena**, revisa días, semanas, meses y gracia y pulsa **Revisar retención**. Las fechas se seleccionan con calendario UTC. Comprueba qué expirará y acepta la revisión antes de **Mover a cuarentena**. Esta operación conserva los objetos durante la gracia y no libera todavía el espacio que ocupan.

El historial de cuarentenas permite **Reanudar movimientos** o **Deshacer cuarentena**. La purga es una acción posterior: espera la gracia desde la finalización, vuelve a verificar un punto retenido y acepta el borrado irreversible antes de **Purgar tras el plazo de gracia**. No se ejecuta retención automática. Catálogos ausentes o contradictorios y operaciones pendientes pueden impedir la limpieza.

## Actualizar o desinstalar

Abre **Aplicación** para revisar los trabajos e **Instalar o actualizar ejecutor**. Al actualizar, se conservan las versiones anteriores y se redirigen solo los horarios propios habilitados. Espera a que terminen trabajadores y reintentos activos. Para retirar, quita primero los horarios, deshabilita los trabajos y revisa cada versión instalada. La versión abierta se retira desde otra copia portable. Se conservan los respaldos, configuración y resultados. [Pasos y límites de desinstalación](INSTALACION.md).

Los formatos anteriores se abren desde **Aplicación → Respaldos de alfa**, según [COMPATIBILIDAD.md](COMPATIBILIDAD.md). Conserva el paquete anterior hasta comprobar la reapertura y una restauración de muestra después de actualizar.

## Alcance y comprobaciones pendientes

La primera versión se limita a carpetas y discos locales NTFS en Windows 11 x64. No incluye VSS, instantáneas coherentes de bases de datos abiertas, vigilancia continua, NAS, nube propia ni sincronización bidireccional. Enlaces, EFS, flujos y archivos de nube no descargados detectados pueden dejar incidencias. Se respalda el contenido y los metadatos admitidos; no equivale a una imagen del sistema ni a conservar todos los permisos o formatos físicos del original.

Las pruebas sintéticas y las compilaciones reproducibles no demuestran durabilidad frente a todos los fallos de hardware. La [matriz de v1](V1.md) distingue software, observaciones de escritorio y validaciones externas. El [piloto](PILOTO.md) registra 7–14 días y los ensayos de entorno sin utilizar respaldos personales.
