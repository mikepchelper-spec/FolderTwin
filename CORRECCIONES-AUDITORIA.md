# Seguimiento de la auditoría integral

El goal activo cubre los trece hallazgos y las mejoras propuestas en [AUDITORIA-2026-11-14.md](AUDITORIA-2026-11-14.md). Parte de `3637142` y del paquete `c90f013`. Las correcciones avanzan por hitos con pruebas sintéticas. La nueva candidata se generará al completar la aceptación del conjunto.

| Hallazgo | Trabajo y aceptación | Estado |
| --- | --- | --- |
| A14-01 | Aislar configuración remota y confirmar dos trabajos locales con pausa de nube dañada | Implementado y probado en desarrollo |
| A14-02 | Abrir FTVERS03 con perfil vacío, leer respaldo protegido, explorar fecha, verificar y recuperar | Motor y WPF probados; exclusión de trabajadores, registro pendiente y registro dañado probados |
| A14-03 | Reservar capacidad para operaciones completas y compactar o segmentar el registro de forma recuperable | Compactación y reserva implementadas; límite de producción e interrupciones probados. Acotar el estado vivo continúa con A14-06 |
| A14-04 | Elegir envío y ligar pausa, cancelación y reintento a su identidad | Implementado, prueba WPF con primera fecha cancelada y segunda controlada |
| A14-05 | Estado remoto, antigüedad, atraso e incidencias en Ruta y bandeja | Implementado y probado. Ruta consulta nube cada diez segundos |
| A14-06 | Acotar catálogos y descriptores tras gracia y purga | Pendiente |
| A14-07 | Conservar y restaurar Zone.Identifier con límites y hash, también a través de nube | Pendiente |
| A14-08 | Recuperación remota con seguimiento, progreso, cancelación y selección de archivo/carpeta | Pendiente |
| A14-09 | Confirmar contraseña y reabrir conexión de lectura antes de activar | Implementado y probado |
| A14-10 | CI de nube con herramientas fijadas, dos ZIP idénticos y pruebas desde extracción | Workflow añadido, validación estática aprobada. Ejecución GitHub pendiente de integrar la rama |
| A14-11 | Coherencia de Ruta/nube, temas, espacio compacto, ayuda y teclado | Paleta común dinámica y pruebas claro/oscuro/alto contraste; escritorio oscuro observado. Pendiente completar agrupación y ayuda remota y mejorar disposición compacta |
| A14-12 | Lecturas remotas acotadas y páginas/filtro de historial | Implementado: salida acotada, cancelación, páginas locales y remotas y filtro de incidencias antiguas probados |
| A14-13 | Identificar paquete abierto/ejecutor y entregar guías coherentes | Pendiente |

Las mejoras complementarias incluyen revisar avisos de dependencias, inventario de componentes, evaluación de firma, políticas de permisos por proveedor y separación gradual de arneses.

## Primer hito

`ProductCloudDispatch.CanSchedule` trata por trabajo los errores de lectura remota. Un registro inválido suspende el despacho remoto de ese trabajo. El monitor sigue observando las carpetas locales. La pausa dañada se conserva. Ruta y bandeja muestran la incidencia al consultar el mismo registro. Una colisión temporal mantiene vivo un monitor que solo necesita automatización de nube.

Ruta muestra el resultado local junto al estado remoto, su fecha y atraso. La bandeja conserva incidencias de credenciales, cuota y registros dañados. También detecta un monitor ausente cuando un trabajo lo necesita para nube.

El formulario permite seleccionar el envío sobre el que actuar. Cada acción vuelve a consultar su ticket. Al crear un repositorio, comprueba la contraseña repetida y la confirmación de haber conservado los datos de recuperación. Cierra la conexión creadora y reabre una de lectura antes de registrar el acceso y activar los envíos.

**Abrir respaldo existente** usa el lector `ProductBackupRecovery` para FTVERS03. Valida identidad, marcador, recibos, catálogos y objetos. Ofrece fechas, búsqueda y páginas de archivos, verificación, recuperación completa o de un archivo, progreso y cancelación. La salida se crea en una carpeta nueva. Una exclusión propia mantiene bloqueados el registro y los trabajadores durante la recuperación y comprueba los orígenes y destinos legibles. Permite registros pendientes. Si una configuración dañada impide comprobar sus rutas, exige crear la salida directamente bajo la raíz de un disco: las raíces de trabajo existentes son subcarpetas dedicadas. Conserva los bytes de la configuración dañada.

## Segundo hito

El registro conserva el límite de 16 MiB y compacta su historia antes de agotar el espacio reservado para una operación. El espejo reserva registro, comienzo y final; la retención reserva expiración y deshacer. El abandono deriva su prueba después de reservar y compactar. La instantánea conserva orden, hashes, expiraciones, abandonos y espejo actual/pendiente. Un plan durable fija los hashes anterior y nuevo; las réplicas se publican por separado y pueden reabrirse durante la transición. Un temporal parcial solo se completa cuando sus bytes coinciden con el prefijo esperado. El estado vivo que crece con las generaciones conservadas sigue teniendo un límite: A14-06 debe retirar metadatos con prueba de purga.

Diez ensayos de transición usan un umbral reducido de 16 KiB con el mismo algoritmo. Cada registro pasó de 22.924 a 3.581 bytes sin cambiar su estado. Dos trabajadores reales terminaron en `PrimarySaved` y `ReplicaPublished`; otra instancia abrió y completó su compactación. Una prueba adicional construyó una cadena válida de 16.775.724 bytes bajo el límite real de producción. La revisión de abandono la redujo a 1.158 bytes, completó el abandono y verificó el punto anterior. Una reserva imposible se rechazó sin modificar las réplicas.

El historial local ofrece páginas de treinta intentos y filtro de incidencias sobre todas las fechas. Conserva selección y foco al llegar un intento o cambiar su resultado. Los envíos remotos tienen páginas de cien. Kopia limita stdout a 16.777.216 caracteres y lee stderr en bloques acotados, incluso sin saltos de línea. Un desbordamiento o cancelación termina el proceso y observa todos sus lectores. El límite produce una incidencia explícita y remite a Kopia independiente para historiales mayores.

Ruta, nube y recuperación independiente comparten paleta dinámica. Las pruebas cambian claro/oscuro dentro de las ventanas sintéticas y comprueban contraste de texto principal y la rama de colores de alto contraste sin cambiar Windows. Se observaron Ruta maximizada, Ruta compacta desplazable y el diálogo de nube con la misma paleta oscura. El tamaño mínimo todavía deja poco espacio para detalles; su mejora y la ayuda/agrupación remota continúan abiertas. La observación no acredita lector de pantalla, DPI real ni cambio del tema del sistema.

## Evidencia de desarrollo

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

1. Resolver el fin de vida de metadatos y medir espacio liberado tras la purga.
2. Acotar el estado vivo del registro junto con ese ciclo.
3. Incorporar Zone.Identifier y completar la recuperación remota.
4. Completar espacio compacto, ayuda remota y guías.
5. Revisar dependencias y firma, producir inventario de componentes, compilar dos distribuciones reproducibles y aceptar el paquete extraído.

Las pruebas externas conservan sus procedimientos pendientes. Incluyen AWS, B2, Filen y MEGA reales, cuotas y revocación reales, Object Lock, otra cuenta/equipo, piloto de 7–14 días, USB físico, ciclos de Windows y lector de pantalla. El goal exige el alcance autónomo implementado y verificado junto con esa matriz explícita.
