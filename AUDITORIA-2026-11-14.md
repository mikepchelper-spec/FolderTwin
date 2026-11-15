# Auditoría integral de FolderTwin del 14 de noviembre de 2026

La prioridad para acercar FolderTwin al uso cotidiano es cerrar recuperación, aislamiento de fallos y crecimiento del historial. La incorporación de nube necesita completar esos contratos y su integración visual. Los mecanismos existentes de identidad, confinamiento, verificación, réplicas y cuarentena ofrecen una base que conviene reutilizar.

Se revisaron las fuentes de `cc22c34`, en `codex/navigation-polish`, y el paquete `c90f013dd6a2a0f580e475f793f7329bfd9ff7f9`. La rama estaba limpia al comenzar y tenía 57 commits locales por delante de su referencia remota. Esta auditoría añade este informe. Las correcciones descritas siguen pendientes.

El ZIP local coincide con SHA-256 `4fd5f351b50016f46b8f5d9fbe23c99f2d790f873ebcc22c3ccc1c2d12613361`. Desde una extracción nueva, `Test-Portable.ps1 -RequireCloudTools` aprobó identidad, fuentes, 504 hashes, inventario, licencias y guías. Se ejecutaron los ensayos `--reader-audit` y `--recovery-audit`, además de dos reproducciones específicas. Se observaron Ruta en tamaño compacto y maximizado y el diálogo de nube mediante una sesión sintética aislada.

La revisión comprende los recorridos principales de motores locales, registro, retención, vigilancia, nube, recuperación, WPF, instalación y distribución. La comprobación de dependencias se centró en fijación, hashes, licencias y documentación oficial de sus contratos. No constituye un análisis exhaustivo de vulnerabilidades del runtime ni una prueba de penetración de los proveedores. Dos revisores auxiliares agotaron su límite de uso antes de terminar sus experimentos. El revisor principal comprobó sus hallazgos por código y ejecutó la reproducción de aislamiento de nube.

**Prioridades.** P1 requiere atención antes de ampliar el piloto de uso cotidiano. P2 corresponde a funcionalidad, claridad o mantenimiento que conviene completar en la siguiente candidata. La evidencia distingue reproducción nueva, recorrido de código, observación de escritorio y mediciones históricas.

| ID | Prioridad | Hallazgo | Evidencia |
| --- | --- | --- | --- |
| A14-01 | P1 | Un registro dañado de pausa de nube termina el monitor local | Reproducido con el binario del ZIP |
| A14-02 | P1 | Falta abrir un respaldo de producto desde una instalación vacía sin exportación previa | Código y contrato documentado |
| A14-03 | P1 | El registro de generaciones tiene un límite definitivo sin rotación | Código |
| A14-04 | P1 | Los controles de nube pueden seguir actuando sobre un envío cancelado antiguo | Código y selección de estados |
| A14-05 | P1 | Los errores de la tercera copia quedan fuera de Ruta y bandeja | Código y observación del diseño |
| A14-06 | P1 para carpetas grandes | La retención conserva metadatos completos de fechas expiradas | Código y medición previa |
| A14-07 | P2 | Archivos descargados con Zone.Identifier quedan rechazados | Reproducción nueva |
| A14-08 | P2 | La recuperación remota larga depende de un diálogo sin progreso ni resultado parcial persistente | Código y escritorio |
| A14-09 | P2 | Falta comprobar la contraseña de recuperación elegida al crear la nube | Código y escritorio |
| A14-10 | P2 | La integración continua no incluye la candidata ni la aceptación de nube | GitHub y workflow actual |
| A14-11 | P2 | Tema, ayuda y disposición de nube difieren de Ruta | Código y escritorio |
| A14-12 | P2 | Historial remoto leído entero y estado local limitado visualmente a 30 intentos | Código |
| A14-13 | P2 | La documentación de entrada mezcla varias candidatas y recorridos | Guías y distribución |

**A14-01. Un fallo de nube puede detener las copias locales.**

`ProductMonitor.RunAsync` calcula `cloudEnabled` con `listing.Jobs.Any(ProductCloudDispatch.HasWork)` antes de observar y despachar los trabajos locales. `HasWork` llama a `IsCloudPaused`, que decodifica el archivo `cloud-connection/paused`. Un archivo truncado o dañado genera `InvalidDataException`. El monitor solo trata como recuperable una colisión de acceso en ese bloque. La excepción termina su bucle. El supervisor reintenta un número acotado de veces, pero vuelve a leer el mismo registro dañado.

La reproducción creó un registro y un trabajo Versionado desechables con vigilancia activada. Escribió `damaged marker` en el registro de pausa de ese trabajo y llamó al monitor del paquete. El resultado fue `InvalidDataException` con la cadena `Decode → IsCloudPaused → HasWork → Enumerable.Any → ProductMonitor.RunAsync`. El archivo sintético del origen permaneció intacto. El ensayo prueba el fallo de inicio del monitor, sin simular suspensión ni reinicio.

La corrección proporcionada consiste en aislar la lectura de nube por trabajo, persistir su incidencia y continuar observando las carpetas locales. También hay que garantizar que las comprobaciones de disponibilidad del monitor usen esa misma política. La regresión debe dejar un trabajo con nube dañada y otro sano, confirmar cambios locales en ambos y comprobar que la incidencia remota sigue visible. [Monitor](experiments/CopyLab/ProductMonitor.cs#L112) · [Consulta de nube](experiments/CopyProbe/ProductCloud.cs#L68)

**A14-02. La recuperación local depende de conservar información fuera del respaldo.**

Los almacenes de producto `FTVERS03` se abren con un `ProductJob` o una configuración exportada. El comando independiente `--recover-version` abre un `LabWorkspace` y los lectores de laboratorio `FTVERS01/02`. Si se pierde C: y la exportación de configuración, los objetos y catálogos intactos en D: carecen de un recorrido de producto que permita seleccionarlos y restaurar su historial. Acumulativo y espejo conservan su árbol actual navegable, pero eso no recupera por sí solo todas las fechas históricas.

Además, `VersionScope.OpenRegisteredRoot` exige que el volumen sea escribible incluso para abrir una recuperación. Un respaldo protegido contra escritura queda rechazado por ese contrato. Estos casos se verificaron mediante recorrido de código, sin montar un volumen ni realizar una prueba física.

Añadir **Abrir respaldo existente** con un lector de recuperación que valide el marcador, las réplicas, los recibos y los objetos disponibles, y permita escribir únicamente en una salida nueva. Debe poder usarse con perfil vacío y origen ausente. La reconstrucción de información de consulta debe evitar habilitar automáticamente un escritor o retención. La prueba de aceptación debe apartar registro, exportación e índice y recuperar dos fechas desde el respaldo conservado. [Recuperación independiente actual](experiments/CopyProbe/VersionRecovery.cs#L9) · [Apertura de producto](experiments/CopyProbe/VersionStore.Product.cs#L19) · [Exigencia de escritura](experiments/CopyProbe/VersionScope.cs#L23) · [Compatibilidad](COMPATIBILIDAD.md)

**A14-03. La vida del almacén queda limitada por un diario de 16 MiB.**

`GenerationRegistry` tiene `MaximumBytes = 16 * 1024 * 1024`. Cada transición conserva el prefijo anterior y añade un registro. Al alcanzar el límite rechaza la siguiente transición. No existe una compactación o segmentación en este recorrido. Expirar puntos añade eventos. Un espejo requiere varios eventos por actualización, de modo que comprobar capacidad para un único evento no asegura espacio para completar toda su operación.

La capacidad es un límite de implementación confirmado, pero no se fabricó en esta auditoría un diario completo cerca del límite. Tampoco se estima cuántos días tardará en alcanzarse, porque depende del uso.

Introducir una compactación durable del registro que preserve generaciones activas, expiraciones relevantes y operaciones pendientes, manteniendo las dos réplicas. Reservar capacidad para la operación completa antes de iniciarla. Hasta disponer de ello, mostrar la proximidad al límite y ofrecer un cambio de destino guiado conservando el anterior. La regresión debe probar el límite exacto y cortes en cada paso de la compactación. [Límite y append](experiments/CopyProbe/GenerationRegistry.cs#L18) · [Rechazo por tamaño](experiments/CopyProbe/GenerationRegistry.cs#L149)

**A14-04. Cancelar una fecha puede ocultar los controles de la siguiente.**

La cola está ordenada por generación. La pantalla y sus botones eligen el primer estado distinto de `Available`. El despachador, en cambio, busca `Pending` o un `Retry` vencido. Una primera fecha cancelada permanece como objetivo de Pausar, Cancelar y Reintentar mientras una segunda fecha se está enviando. Los controles actúan sobre el registro que el código seleccionó, sin ofrecer al usuario elección de fecha.

Mostrar una lista acotada de envíos con fecha y estado y ligar cada acción al identificador seleccionado. La acción rápida del envío actual debe apuntar al trabajador activo o al siguiente elegible. Mantener los cancelados consultables en historial. Probar dos fechas, cancelar la primera y controlar la segunda. [Controles y estado](experiments/CopyLab/ProductCloudWindow.xaml.cs#L213) · [Cola](experiments/CopyProbe/CloudQueue.cs#L40) · [Despacho](experiments/CopyProbe/ProductCloud.cs#L84)

**A14-05. El estado principal puede parecer correcto mientras la nube requiere atención.**

`RouteState` y el resumen de bandeja consultan resultados locales y vigilancia. No incorporan las incidencias de la cola remota. Los textos «Completo» y «Verificado» describen la copia local, pero la pantalla no hace visible al lado que una cuota agotada o una credencial rechazada mantienen atrasada la tercera copia. La ventana de nube actualiza ese estado al abrirse o al pulsar Actualizar envíos.

Presentar dos estados por trabajo, **Copia local** y **Copia en nube**, con la fecha del último punto de cada una. La bandeja debe señalar cualquier incidencia pendiente y llevar a su solución. Un fallo remoto debe conservar la confirmación local y mostrar el atraso remoto explícitamente. Probar copia local correcta más credencial remota rechazada, actualización automática de la vista y resolución posterior. [Estado de Ruta](experiments/CopyLab/ProductWindow.Runs.cs#L183) · [Bandeja](experiments/CopyLab/TraySnapshot.cs#L64) · [Estado remoto](experiments/CopyLab/ProductCloudWindow.xaml.cs#L226)

**A14-06. La retención de contenido no acota el crecimiento de sus metadatos.**

El inventario de retención exige todos los catálogos registrados, incluidos los expirados, y lee sus descriptores. La purga elimina objetos en cuarentena, conservando esos metadatos. La medición anterior de 329.000 archivos registró aproximadamente 147 MB en dos catálogos y 73 MB de diarios por punto. Siete puntos totalizaron unos 1,54 GB aunque los objetos sumaran 447 bytes. Son cifras históricas de `CAPACIDAD.md`, no una medición repetida hoy.

Definir el fin de vida de catálogos y descriptores cuando una expiración haya terminado su gracia y purga. Conservar comprobantes compactos suficientes para detectar desapariciones inesperadas y operaciones incompletas. Medir primero qué registros siguen siendo necesarios para recuperar, deshacer y auditar. La interfaz debe desglosar datos, metadatos y cuarentena al estimar espacio liberable. Evitar presentar una purga pequeña de objetos como solución al coste dominante de metadatos. [Inventario de retención](experiments/CopyProbe/Retention.cs#L110) · [Purga](experiments/CopyProbe/RetentionCleanup.cs#L78) · [Medición](CAPACIDAD.md#L19)

**A14-07. Un documento descargado puede impedir confirmar el respaldo.**

`RequireSupportedData` rechaza cualquier flujo alternativo. Se añadió `Zone.Identifier` con `ZoneId=3` a un archivo de texto sintético y se ejecutó `ProductRunner.Run`. El resultado fue `Review` y el mensaje «data.txt tiene flujos de datos alternativos. Requiere revisión». Esta es una limitación intencional del alcance actual, pero afecta documentos habituales de Descargas y Escritorio. Microsoft documenta el uso de ese flujo para marcar descargas. [Código](experiments/CopyProbe/WindowsProbeNative.cs#L188) · [Documentación de Microsoft](https://techcommunity.microsoft.com/blog/microsoftdefenderatpblog/hunting-tip-of-the-month-browser-downloads/220454)

Definir soporte explícito para conservar y restaurar `Zone.Identifier`, con límites de tamaño y comprobación de integridad, y mantener rechazo de otros flujos hasta admitirlos. Conservar esta marca evita retirar una señal de seguridad de Windows. Mientras se implementa, el análisis previo debe explicar el caso con lenguaje cotidiano y nombrar el archivo afectado. La aceptación debe incluir copia local, exportación a nube y recuperación de ese metadato.

**A14-08. Recuperar desde nube carece de seguimiento persistente.**

El diálogo de nube se abre modal. La restauración ocurre dentro de él, tiene un límite de dos horas y se cancela al cerrarlo. No hay progreso de archivos o bytes, ni una acción explícita de cancelación con el mismo comportamiento de la recuperación local. La ruta de salida se comunica al terminar correctamente. Un fallo o cierre puede dejar una carpeta parcial cuya ubicación no queda en el resultado mostrado al volver.

Reutilizar el ejecutor de recuperación local y su registro de progreso. Mostrar desde el inicio la carpeta de salida, fecha seleccionada y tamaño esperado, y conservar el resultado parcial con una acción para inspeccionarlo. Permitir recuperar un archivo o subcarpeta es una mejora posterior útil para evitar descargar todo el punto. Kopia admite restaurar una ruta interna de una instantánea. [Controlador](experiments/CopyLab/ProductCloudWindow.xaml.cs#L149) · [Entrada modal](experiments/CopyLab/ProductWindow.Cloud.cs#L13) · [Contrato de Kopia](https://kopia.io/docs/reference/command-line/common/snapshot-restore/)

**A14-09. La contraseña elegida necesita una comprobación de recuperación.**

Crear un repositorio usa una única PasswordBox, sin repetición ni una verificación de los datos que el usuario guardó fuera del equipo. Un error al escribir puede quedar oculto mientras el perfil local conserve la credencial protegida. El riesgo aparece al intentar recuperar tras perder ese perfil.

Para crear un repositorio, pedir confirmación de contraseña y ofrecer un paso breve para conservar ubicación y datos de recuperación. Comprobar una apertura de solo lectura introduciendo esos datos nuevamente. DPAPI es apropiado para las credenciales locales, pero su recuperación normalmente depende de la cuenta y el equipo. El procedimiento de emergencia debe contemplarlo. [Campo y creación](experiments/CopyLab/ProductCloudWindow.xaml#L36) · [DPAPI](https://learn.microsoft.com/en-us/windows/win32/api/dpapi/nf-dpapi-cryptprotectdata)

**A14-10. La CI no protege las funciones de nube añadidas.**

La consulta de GitHub mostró como última validación aprobada del workflow general la ejecución `42212336681`, sobre `d902f4c`, del 6 de noviembre. Las fuentes locales `cc22c34` y la candidata de nube `c90f013` cuentan con evidencia local. El workflow actual llama a `Publish-Portable.ps1` sin herramientas de nube y omite `--product-cloud-smoke`, `Test-CloudPoint.ps1` y `--cloud-faults`. Publicar la rama por sí solo no ejecutaría esas pruebas.

Añadir un job acotado para nube usando herramientas fijadas por hash, S3 en loopback y cuentas sintéticas. Probar desde el ZIP extraído. La prueba de 329.000 archivos puede quedar bajo ejecución manual para controlar costes. Exigir el resultado del commit elegido antes de la siguiente entrega pública. [Workflow](.github/workflows/copy-probe.yml#L103) · [Última ejecución observada](https://github.com/mikepchelper-spec/FolderTwin/actions/runs/42212336681)

**A14-11. Completar Ruta requiere coherencia entre pantallas y ayuda.**

En escritorio se observó Ruta clara y verde junto a un diálogo de nube oscuro con botones y espaciado de otro estilo. `ProductWindow` fuerza tema Light y su paleta es estática, mientras el diálogo hereda el tema general. A 600 × 500, la lista, cabecera y acciones fijas dejan una franja muy pequeña para los detalles desplazables. A tamaño maximizado, las mismas fechas y estados se repiten en tarjeta y texto inferior.

El botón Copia en nube y su formulario tampoco participan en el registro de ayuda contextual que ya explica los controles locales. Región, bucket, clave del proveedor y contraseña del repositorio aparecen juntos. Las pruebas de humo rellenan campos directamente y no acreditan navegación real con lector de pantalla.

Mantener Ruta como diseño único y compartir colores, tipografía, botones, estados y cabeceras entre todas las ventanas. Usar recursos intercambiables para claro, oscuro y alto contraste. En pantallas pequeñas, dejar un único desplazamiento del detalle y reducir las áreas fijas. Agrupar configuración de nube en proveedor, acceso y recuperación, con ayuda breve junto a cada campo. El usuario debe reconocer el propósito de cada contraseña. Extender las pruebas de teclado y foco existentes al diálogo nuevo. [Paleta](experiments/CopyLab/ProductRoute.xaml#L3) · [Ventana](experiments/CopyLab/ProductWindow.xaml#L6) · [Formulario](experiments/CopyLab/ProductCloudWindow.xaml) · [Ayuda existente](experiments/CopyLab/ProductWindow.Help.cs)

Filen requiere obtener una clave API mediante su CLI y actualizarla junto con la contraseña cuando esta cambia. MEGA tiene particularidades documentadas de nombres duplicados y sesiones. La ayuda debe presentar los requisitos del perfil elegido y mantener visibles sus pendientes de validación real. [Filen](https://rclone.org/filen/) · [MEGA](https://rclone.org/mega/)

**A14-12. Los historiales necesitan límites visibles y lectura acotada.**

La vista local usa `Take(30)` sin paginación ni texto que anuncie el límite. Una incidencia anterior puede dejar de aparecer aunque su registro siga en disco. En nube, el listado pide todas las instantáneas como JSON, usa `ReadToEndAsync` y crea un DOM completo antes de ordenarlo. Existe un plazo de operación, pero no un límite de bytes de stdout ni paginación. No se reprodujo agotamiento de memoria.

Añadir «Últimos 30 intentos», navegación por páginas y filtro de incidencias en la vista local. Para nube, medir un historial sintético largo y limitar tamaño de respuesta y resultados mostrados. Reutilizar la cola y el lector existentes, evitando un segundo catálogo autoritativo. [Historial local](experiments/CopyLab/ProductWindow.Runs.cs#L145) · [Listado remoto](experiments/CopyProbe/KopiaRepository.cs#L44) · [Captura de salida](experiments/CopyProbe/KopiaRepository.cs#L140)

**A14-13. La entrada al producto debe identificar una candidata inequívoca.**

README presenta RC2 pública, varias candidatas históricas, el acceso `598fbc7` y la nube `c90f013`. La guía remite a artefactos de Actions, mientras la candidata de nube se entrega localmente y esas compilaciones de CI no incluyen sus herramientas. SECURITY enlaza la aceptación histórica de RC1 como referencia de la revisión. Esto complica saber qué ejecutable abrir o qué funciones esperar.

Ofrecer al principio una tabla breve con versión pública, candidata local, funciones y evidencia aplicable. Mover el recorrido histórico a los informes ya existentes. Mostrar en Ayuda y versión el ejecutable abierto, el ejecutor instalado y si pertenecen al mismo paquete. Incluir en el ZIP una guía autónoma de nube y recuperación de emergencia, con identidad de compilación y sin su informe posterior. [README](README.md#L13) · [Guía](GUIA.md#L17) · [Política de seguridad](SECURITY.md#L3) · [Guías distribuidas](scripts/Publish-Portable.ps1#L96)

La seguridad que conviene conservar incluye origen de solo lectura, apertura de rutas por handles, identidades de volumen y directorio, rechazo de inventarios incompletos, copia temporal verificada, catálogos duplicados, bloqueo de limpieza ante contradicciones y cuarentena recuperable. En nube se usan credenciales protegidas, argumentos estructurados, entorno del hijo para secretos, herramientas con hashes fijados y autenticación efímera de loopback. Las acciones de GitHub están fijadas por commit y el token tiene permisos de lectura. Las pruebas de lectores y recuperación aprobadas hoy sostienen esas comprobaciones concretas.

Para seguridad de distribución, mantener una revisión periódica de avisos del runtime, Kopia y rclone con versión y decisión registradas. Antes de una distribución general, evaluar firma Authenticode del ejecutable y un manifiesto de componentes. Para permisos remotos, preparar políticas verificables de creación/envío, lectura y mantenimiento, y probar sus operaciones mínimas. La protección frente a borrado exige verificar permisos, versiones y retención con cada proveedor. Los pendientes actuales de Object Lock son correctos y deben conservar ese estado hasta disponer de evidencia.

El trabajo de mantenimiento más rentable es separar gradualmente pruebas de producto cuando se modifique cada área. Actualmente conviven en `experiments/CopyProbe` y `CopyLab`, y el ejecutable de producto reconoce numerosas opciones de laboratorio. Esa estructura dificulta revisar el alcance de una entrega. Una separación pequeña de arneses y entradas reducirá la superficie que se distribuye. No se encontró una necesidad demostrada de introducir microservicios, un sistema de plugins o una reescritura del motor.

El orden de trabajo recomendado es el siguiente.

1. Corregir A14-01, A14-04 y A14-05 y añadir sus regresiones. El usuario debe ver el fallo remoto y las copias locales deben continuar.
2. Completar A14-02 y A14-09. Probar recuperación con pérdida del perfil y con los datos de emergencia conservados fuera del equipo.
3. Resolver A14-03 y A14-06 con fixtures cerca de los límites y cortes de proceso. Medir espacio realmente liberado y tiempos después de un historial largo.
4. Completar A14-07, A14-08, A14-11 y A14-12. Priorizar documentos habituales, progreso, errores, ayuda y accesibilidad sobre nuevas skins.
5. Incorporar aceptación de nube en CI, aclarar distribución y preparar una candidata única. Ejecutar después las pruebas manuales y de proveedores según su matriz.

La aceptación externa pendiente comprende cuentas reales de AWS, B2, Filen y MEGA, revocación y cuotas reales, recuperación desde otro equipo, protección contra borrado, piloto de 7–14 días, USB físico, reinicio/suspensión/sesión y accesibilidad con lector y escalados adicionales. El ensayo de procesos sintéticos y la revisión estática no acreditan esas condiciones.

Para reproducir los dos hallazgos nuevos de laboratorio, crear un registro sintético, un origen con `data.txt` y un destino dedicado mediante `ProductJobRegistry.Create`, habilitar `ProductWatchMode.Watching` y conservar las referencias del trabajo. En A14-01, crear `cloud-connection/paused` bajo ese trabajo con contenido inválido y llamar a `ProductMonitor.RunAsync` en modo no programado. Debe registrarse el fallo remoto y continuar el monitor, pero actualmente lanza `InvalidDataException`. En A14-07, escribir el flujo `data.txt:Zone.Identifier` con `[ZoneTransfer]` y `ZoneId=3`, ejecutar `ProductRunner.Run` y comprobar el resultado `Review`. El arnés de esta revisión llamó por reflexión a los ensamblados del ZIP y mantuvo toda la configuración en carpetas temporales desechables.

El ensayo general `--reader-audit` aprobó 24 casos semánticos. `--recovery-audit`, semilla 58203, aprobó 40 selecciones de calendario, seis puntos y recuperación de objetos compartidos con cuarentena, deshacer, purga y corrupción. Su éxito corresponde al contrato que ejercitan esos arneses. A14-02 describe la entrada de recuperación de producto que esos ensayos no cubren.
