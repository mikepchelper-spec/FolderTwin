# Seguimiento de la auditoría integral

El goal activo cubre los trece hallazgos y las mejoras propuestas en [AUDITORIA-2026-11-14.md](AUDITORIA-2026-11-14.md). Parte de `3637142` y del paquete `c90f013`. Las correcciones avanzan por hitos con pruebas sintéticas. La nueva candidata se generará al completar la aceptación del conjunto.

| Hallazgo | Trabajo y aceptación | Estado |
| --- | --- | --- |
| A14-01 | Aislar configuración remota y confirmar dos trabajos locales con pausa de nube dañada | Implementado y probado en desarrollo |
| A14-02 | Abrir FTVERS03 con perfil vacío, leer respaldo protegido, explorar fecha, verificar y recuperar | Motor y WPF probados; exclusión de trabajadores, registro pendiente y registro dañado probados |
| A14-03 | Reservar capacidad para operaciones completas y compactar o segmentar el registro de forma recuperable | Implementado y probado: reserva, compactación y retirada de expectativas purgadas con comprobantes |
| A14-04 | Elegir envío y ligar pausa, cancelación y reintento a su identidad | Implementado, prueba WPF con primera fecha cancelada y segunda controlada |
| A14-05 | Estado remoto, antigüedad, atraso e incidencias en Ruta y bandeja | Implementado y probado. Ruta consulta nube cada diez segundos |
| A14-06 | Acotar catálogos y descriptores tras gracia y purga | Implementado y probado en desarrollo; medición de 4.096 archivos, tres modos, interrupciones y WPF |
| A14-07 | Conservar y restaurar Zone.Identifier con límites y hash, también a través de nube | Implementado y probado en desarrollo: tres modos, catálogos v4, S3/rclone sintéticos y compatibilidad |
| A14-08 | Recuperación remota con seguimiento, progreso, cancelación y selección de archivo/carpeta | Implementado. Prueba de proceso real, control desde otro registro y selección/reapertura WPF en desarrollo |
| A14-09 | Confirmar contraseña y reabrir conexión de lectura antes de activar | Implementado y probado |
| A14-10 | CI de nube con herramientas fijadas, dos ZIP idénticos y pruebas desde extracción | Workflow añadido, validación estática aprobada. Ejecución GitHub pendiente de integrar la rama |
| A14-11 | Coherencia de Ruta/nube, temas, espacio compacto, ayuda y teclado | Ayuda y grupos de nube, acciones secundarias plegables y teclado/foco probados en desarrollo. Paleta común claro/oscuro y alto contraste sintético; pendiente revisión del conjunto tras A14-08 |
| A14-12 | Lecturas remotas acotadas y páginas/filtro de historial | Implementado: salida acotada, cancelación, páginas locales y remotas y filtro de incidencias antiguas probados |
| A14-13 | Identificar paquete abierto/ejecutor y entregar guías coherentes | Interfaz y guías actualizadas. Pendiente comprobar la identidad leyendo paquetes extraídos y ejecutores instalados |

Las mejoras complementarias incluyen revisar avisos de dependencias, inventario de componentes, evaluación de firma, políticas de permisos por proveedor y separación gradual de arneses.

El inventario CycloneDX se genera junto al paquete. La revisión del 16 de noviembre fija SDK 10.0.404 y runtime 10.0.15, mantiene las versiones de Kopia y rclone, deja las políticas de permisos para validar por proveedor y registra la decisión de firma. La prueba de procesos y cancelación remota vive en `AutomationAcceptance`, recompilada contra el motor extraído y con verificación de identidad del binario. [Seguridad de distribución](SEGURIDAD-DISTRIBUCION.md)

## Primer hito

`ProductCloudDispatch.CanSchedule` trata por trabajo los errores de lectura remota. Un registro inválido suspende el despacho remoto de ese trabajo. El monitor sigue observando las carpetas locales. La pausa dañada se conserva. Ruta y bandeja muestran la incidencia al consultar el mismo registro. Una colisión temporal mantiene vivo un monitor que solo necesita automatización de nube.

Ruta muestra el resultado local junto al estado remoto, su fecha y atraso. La bandeja conserva incidencias de credenciales, cuota y registros dañados. También detecta un monitor ausente cuando un trabajo lo necesita para nube.

El formulario permite seleccionar el envío sobre el que actuar. Cada acción vuelve a consultar su ticket. Al crear un repositorio, comprueba la contraseña repetida y la confirmación de haber conservado los datos de recuperación. Cierra la conexión creadora y reabre una de lectura antes de registrar el acceso y activar los envíos.

**Abrir respaldo existente** usa el lector `ProductBackupRecovery` para FTVERS03. Valida identidad, marcador, recibos, catálogos y objetos. Ofrece fechas, búsqueda y páginas de archivos, verificación, recuperación completa o de un archivo, progreso y cancelación. La salida se crea en una carpeta nueva. Una exclusión propia mantiene bloqueados el registro y los trabajadores durante la recuperación y comprueba los orígenes y destinos legibles. Permite registros pendientes. Si una configuración dañada impide comprobar sus rutas, exige crear la salida directamente bajo la raíz de un disco: las raíces de trabajo existentes son subcarpetas dedicadas. Conserva los bytes de la configuración dañada.

## Segundo hito

El registro conserva el límite de 16 MiB y compacta su historia antes de agotar el espacio reservado para una operación. El espejo reserva registro, comienzo y final; la retención reserva expiración y deshacer. El abandono deriva su prueba después de reservar y compactar. La instantánea conserva orden, hashes, expiraciones, abandonos, comprobantes de purga y espejo actual/pendiente. Un plan durable fija los hashes anterior y nuevo; las réplicas se publican por separado y pueden reabrirse durante la transición. Un temporal parcial solo se completa cuando sus bytes coinciden con el prefijo esperado. Las generaciones definitivamente purgadas salen del estado vivo mediante el tercer hito. Si las generaciones conservadas, abandonos y comprobantes todavía exceden su capacidad, rechaza la operación con una incidencia; no ofrece capacidad infinita.

Diez ensayos de transición usan un umbral reducido de 16 KiB con el mismo algoritmo. Cada registro pasó de 22.924 a 3.581 bytes sin cambiar su estado. Dos trabajadores reales terminaron en `PrimarySaved` y `ReplicaPublished`; otra instancia abrió y completó su compactación. Una prueba adicional construyó una cadena válida de 16.775.724 bytes bajo el límite real de producción. La revisión de abandono la redujo a 1.158 bytes, completó el abandono y verificó el punto anterior. Una reserva imposible se rechazó sin modificar las réplicas.

El historial local ofrece páginas de treinta intentos y filtro de incidencias sobre todas las fechas. Conserva selección y foco al llegar un intento o cambiar su resultado. Los envíos remotos tienen páginas de cien. Kopia limita stdout a 16.777.216 caracteres y lee stderr en bloques acotados, incluso sin saltos de línea. Un desbordamiento o cancelación termina el proceso y observa todos sus lectores. El límite produce una incidencia explícita y remite a Kopia independiente para historiales mayores.

Ruta, nube y recuperación independiente comparten paleta dinámica. Las pruebas cambian claro/oscuro dentro de las ventanas sintéticas y comprueban contraste de texto principal y la rama de colores de alto contraste sin cambiar Windows. Se observaron Ruta maximizada, Ruta compacta desplazable y el diálogo de nube con la misma paleta oscura. El tamaño mínimo todavía deja poco espacio para detalles; su mejora y la ayuda/agrupación remota continúan abiertas. La observación no acredita lector de pantalla, DPI real ni cambio del tema del sistema.

## Tercer hito

La purga manual retira los dos catálogos, el descriptor completo, la ubicación y el recibo de las fechas expiradas. Retira también las listas de origen/baseline que conserve su operación de espejo aplicada. Solo comienza después de la gracia y de revisar catálogos, identidades y objetos activos. Antes de borrar esos metadatos publica dos comprobantes con nombres permitidos, identidades, tamaños y hashes y registra su huella en ambas réplicas del registro. Cada generación retirada sale de las expectativas vivas. Los comprobantes, planes de retención, listas de objetos purgados y pequeños estados de espejo se conservan para auditar y autorizar una recopia posterior. Los planes abandonados conservan su información de diagnóstico.

Una fase interrumpida aparece como purga pendiente y bloquea nuevas escrituras y limpiezas hasta reanudarla. Una prueba ausente, dañada o contradictoria conserva ese bloqueo. La recuperación de una fecha retenida sigue disponible. La purga final retira la fecha del explorador, aunque todo su contenido estuviera compartido con otras fechas. Deshacer sigue disponible durante la cuarentena, antes de comenzar la purga. Una purga de objetos terminada con un paquete anterior conserva sus metadatos hasta que se vuelve a autorizar explícitamente su limpieza.

`--retention` aprobó las diez transiciones de purga, los cuatro cortes de cada evento replicado de prueba/retirada, cinco mutaciones semánticas con checksum recalculado, pérdida/corrupción de comprobantes, retroceso de reloj, reapertura sin origen y una terminación real después de borrar el primer metadato. La prueba cerca de 16 MiB conservó la huella de una purga al compactar. También se verificó la finalización explícita del estado de purga anterior.

`--retention-scale` midió ocho puntos de 4.096 archivos en C:, conservando tres. El tamaño lógico pasó de 21.842.903 a 8.221.658 bytes, incluidos los nuevos comprobantes: 13.621.245 bytes netos liberados. Los candidatos de metadatos sumaban 13.644.005 bytes. Revisión, cuarentena y purga tardaron 0,265 / 0,767 / 0,690 s. Son mediciones de desarrollo con cachés; no miden transferencia física ni escalan por sí solas a 329.000 archivos.

`--storage-safety` aprobó retirar dos fechas en cada modo, restaurar el punto retenido con el origen apartado y confirmar la siguiente copia automática. Las regresiones de disco lleno y catálogos contradictorios también pasaron. `--product-retention-smoke` aprobó el desglose de metadatos, la revisión, deshacer, rechazo por gracia, purga desde WPF y deshabilitación de una purga ya completada. [Mediciones y límites](CAPACIDAD.md#purga-de-metadatos-en-desarrollo).

## Evidencia de desarrollo

El registro de recuperaciones remotas se abre desde cualquier perfil de FolderTwin, incluido uno sin trabajos. Cada intento fija el repositorio, fecha, selección e identidad del padre de salida. Guarda sus credenciales bajo DPAPI y autoriza un proceso separado. Una orden persistente permite parar Kopia durante una espera de red. El resultado y las fases se consultan después de cerrar la ventana. Una descarga interrumpida mantiene su salida identificada. Los nuevos intentos eligen otra carpeta. La configuración rechaza altas o cambios de raíz que se solapen con una recuperación activa.

`--cloud-recovery-workers` registró un hijo real retenido en loopback, cancelación desde un segundo registro, terminación real, progreso y salida persistentes, otra copia local confirmada y rechazo de raíz sustituida u ocupada. La primera lectura después de terminar el proceso puede coincidir con la liberación de handles por Windows. La prueba espera ese cambio observando el mismo intento. `--product-cloud-smoke` aprobó dos fechas, selección por archivo con marca, carpeta con subcarpeta vacía y el mismo intento visible después de cerrar y reabrir la interfaz. [Guía de recuperación](RECUPERACION-NUBE.md)

El cuarto hito conserva `Zone.Identifier` como metadato de ruta en catálogo v4 y mantiene CAS sin ADS. `--zone-native`, `--zone-local` y `--product-watch` probaron los límites, vacío/ausencia, bytes arbitrarios, un único objeto para marcas distintas, cambios exclusivos de marca, interrupción/reanudación con origen apartado, retención y rechazo de catálogos alterados. El ensayo nativo detectó que BackupWrite puede escribir desde un handle base de lectura; el adaptador ahora exige permiso efectivo de escritura antes de usarlo. [Contrato y evidencia de Kopia](experiments/CopyProbe/ZONE-IDENTIFIER.md).

Kopia 0.23.1 perdió las marcas en su restauración directa. Los envíos nuevos incluyen el catálogo confirmado dentro del repositorio cifrado, usan etiquetas que los lectores antiguos no enumeran y preservan los puntos remotos anteriores. La recuperación comprueba catálogo, inventario, SHA-256 y marca antes de promover cada archivo. `--cloud-point`, S3 de loopback y los perfiles rclone aprobaron marcas vacías, ausentes, distintas y de 16 KiB, selección por archivo/carpeta, cancelación, catálogo incorrecto y compatibilidad. El motor ya informa fases y recuentos; la interfaz persistente de A14-08 continúa abierta.

Ruta agrupa las acciones secundarias en **Más acciones** y conserva su expansión si contiene el foco durante un cambio de tamaño. La prueba espera el cierre animado de Fluent antes de medir: obtuvo 131,9 DIP de detalles en 600×500 y 1040×500, y 251,9 DIP en 760×620. Los mensajes de operación tienen desplazamiento acotado. Nube agrupa ubicación, acceso y clave de recuperación, añade ayuda contextual y devuelve el foco al cerrar su ayuda. `--product-usability-smoke` aprobó cien controles de Ruta, ambos temas y el recorrido de los tres modos. `--product-cloud-smoke` aprobó la ayuda remota y sus recorridos existentes. Estos resultados no acreditan DPI real ni lector de pantalla.

Las regresiones `--batch`, `--accumulative`, `--mirror`, `--resume`, `--reader-audit`, `--product-backup-recovery` y `AutomationAcceptance --cloud-faults` pasaron durante este hito. El paquete final sigue pendiente. La revisión automática rechazó por política retirar una carpeta temporal del ensayo S3; se conserva fuera del repositorio como `FolderTwin-CloudS3-5yw1wsdp.fff` bajo la carpeta temporal. No se volvió a intentar su borrado.

[Salida compacta de los ensayos del cuarto hito](experiments/CopyProbe/zone-support-evidence.txt). Incluye recuperación remota anterior y nueva, límites, incidencias, procesos y medidas de interfaz. La revisión semántica rechaza totales remotos negativos o excesivos, etiquetas mezcladas y un sobre nuevo presentado como recuperación antigua sin metadatos.

- Compilación WPF y motor con SDK 10.0.403, sin errores ni avisos.
- `CopyProbe --product-backup-recovery` aprobó dos fechas de los tres modos con origen y registro apartados, escritura denegada por ACL en el respaldo, comparación de bytes del respaldo, réplica dañada, registro de generaciones dañado, objeto corrupto, marcador sustituido y salida ocupada.
- `FolderTwin --product-monitor-smoke` aprobó los tres modos, cambios, destino ausente/reconectado, pausa, restauración sin origen y dos confirmaciones locales después de dañar la pausa remota. La incidencia de nube permaneció visible.
- `FolderTwin --product-cloud-smoke` aprobó confirmación de contraseña, reapertura independiente, control del ticket seleccionado y dos restauraciones remotas desde perfil vacío. Aprobó también estado de cuota en Ruta/bandeja y recuperación local WPF sin origen, registro anterior ni exportación.
- `FolderTwin --product-usability-smoke` aprobó 99 controles con ayuda contextual, F1, diagnóstico revisable, asistente de los tres modos, verificación/recuperación y foco en ambos temas del ensayo.
- `FolderTwin --product-surface-smoke` aprobó páginas, filtro de una incidencia anterior a 65 intentos y estabilidad de selección/foco ante un nuevo intento y un cambio de resultado, además de los recorridos de teclado claro/oscuro.
- `CopyProbe --registry-checkpoint` aprobó diez transiciones, rechazo por capacidad y dos terminaciones reales. `--version-abandon` aprobó también la prueba cerca de 16 MiB.
- `CopyProbe --registry`, `--retention`, `--mirror`, `--product-store`, `--reader-audit` y `--product-backup-recovery` aprobaron las regresiones afectadas. La última incluye exclusión de trabajadores, registro pendiente y configuración dañada conservada.
- `AutomationAcceptance --cloud-faults` aprobó stdout desbordado, stderr sin saltos, clasificación entre bloques y cancelación, junto con las regresiones de conexión, cuota, credenciales, duplicados, corrupción y terminación de hijos.
- `cloud-acceptance.yml` pasó actionlint 1.7.12 y el parser PowerShell de sus cinco bloques. Herramientas y licencia coinciden con los hashes fijados del producto.

El arnés de lectura independiente corrigió una referencia mutable de permisos dentro de su prueba. Conserva el descriptor de seguridad como texto antes de aplicar la ACL sintética y lo restaura al salir. Si falla, conserva su carpeta para diagnóstico.

La aceptación desde el ZIP reproducible final sigue pendiente.

## Continuación

1. Revisar interfaz y guía del recorrido completo. Comprobar identidad de paquete y ejecutor de A14-13 desde extracción.
2. Completar la validación de permisos mínimos sintéticos y el inventario de componentes.
3. Revisar dependencias y firma, producir inventario de componentes, compilar dos distribuciones reproducibles y aceptar el paquete extraído.

Las pruebas externas conservan sus procedimientos pendientes. Incluyen AWS, B2, Filen y MEGA reales, cuotas y revocación reales, Object Lock, otra cuenta/equipo, piloto de 7–14 días, USB físico, ciclos de Windows y lector de pantalla. El goal exige el alcance autónomo implementado y verificado junto con esa matriz explícita.
