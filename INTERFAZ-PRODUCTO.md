# Candidata local de interfaz

Alcance de software del 6 de noviembre de 2026. Se parte de RC2 y de la corrección local de navegación `4ba14cb`. La distribución pública de RC2 y su acceso directo se conservan. Esta entrega local completa los recorridos de producto sobre los motores existentes.

La aceptación del paquete y sus hashes se registran después de compilar, fuera del ZIP. Este documento sirve como matriz de requisitos y protocolo reproducible.

## Matriz

| ID | Requisito | Implementación y evidencia exigida |
|---|---|---|
| UI-01 | Trabajos como pantalla principal y acciones habituales visibles | `ProductWindow.xaml` fija Respaldar y Recuperar encima de los detalles. UsabilitySmoke desplaza los detalles y comprueba que ambas acciones queden en pantalla a 600 × 500. LayoutSmoke comprueba 1040 × 740, 760 × 620 y 600 × 500. |
| UI-02 | Identificar pantalla y regresar sin desplazamiento | Cabecera de Trabajos, Aplicación, Importar, Recuperar y Nuevo trabajo. SurfaceSmoke recorre los retornos superiores en claro y oscuro, comprobando ubicación, selección y foco. |
| UI-03 | Atrás y Cancelar en el asistente | Cuatro pasos con acciones fuera del scroll. SurfaceSmoke comprueba error de hora, pasos, regreso y cancelación. |
| UI-04 | Primer uso desde vacío | FirstBackupButton abre el mismo asistente. UsabilitySmoke inicia con registro vacío y crea los tres modos con los selectores inyectados del fixture. |
| UI-05 | Explicar modo y primera copia | Ejemplos por modo, revisión antes de registrar, mensaje al guardar y próximo paso persistido según respaldo, verificación y recuperación. UsabilitySmoke exige cada transición. |
| UI-06 | Inventario de controles y ayuda | `ProductWindow.Help.cs` tiene una entrada por control. UsabilitySmoke exige tooltip y HelpText para los 85 controles de interacción. [Inventario](AYUDA-CONTROLES.md) |
| UI-07 | Ayuda por mouse, clic y teclado | ToolTip con ShowOnDisabled y contenido actualizado al abrir. Botón ⓘ y F1 abren ayuda por control. UsabilitySmoke abre el tooltip, usa F1, abre por clic y comprueba retorno del foco al cerrar. |
| UI-08 | Requisitos de acciones deshabilitadas | Cada entrada incluye requisitos. Si hay una comprobación activa explica la espera. El usuario puede consultar la ayuda en su ventana aunque el control esté deshabilitado. Se exige el requisito de elegir carpeta en Siguiente deshabilitado. |
| UI-09 | Estado por trabajo y fechas separadas | Runs muestra origen, destino, último respaldo completo, última verificación, operación y reintentos. Settings muestra horario observado y espacio del destino. TrayAudit comprueba incidentes de respaldo y verificación independientes. |
| UI-10 | Ausencia, ocupado, parcial y revisión | Mensajes conservan la acción de resolver y el estado parcial. UsabilitySmoke usa archivo ocupado, origen ausente, destino ausente y raíz sustituida. La presentación de disco lleno se inyecta como resultado con código 0x80070070. El agotamiento físico de un volumen desechable conserva su evidencia VHDX de RC2. |
| UI-11 | Resolver incidencia sin alterar la fecha del respaldo anterior | UsabilitySmoke verifica un punto con origen ausente y exige que el fallo de respaldo siga visible con su fecha anterior. Después devuelve las raíces y reintenta con éxito. |
| UI-12 | Progreso y controles | RunSmoke y RestoreSmoke comprueban pausa, continuación, cancelación, cierre de interfaz y lectura del resultado al reabrir, mediante trabajadores reales del fixture. |
| UI-13 | Buscar, navegar, fechas, diferencias y eliminados | ProductSmoke conserva búsqueda Unicode, navegación paginada, comparación y recuperación de eliminado desde un punto previo. ConfigurationSmoke comprueba recuperación sin origen. |
| UI-14 | Recuperación revisada en salida nueva | UsabilitySmoke exige resumen antes de recuperar y compara contenido conocido y carpeta vacía por cada modo. RestoreSmoke comprueba interrupciones y exclusiones. |
| UI-15 | Abrir carpeta recuperada | Dos botones usan únicamente una recuperación confirmada del trabajo seleccionado. Se valida el padre y se abre una ruta de Windows obtenida desde el handle. La prueba intercepta la apertura del Explorador y compara identidades de directorio. |
| UI-16 | Recuperación con índice no disponible | ProductSmoke y VersionWindow.Smoke conservan fallback ante índice dañado y pérdida parcial de caché, preservación de diarios y reconstrucción de caché ausente. |
| UI-17 | Espejo y revisiones obsoletas | El botón Aplicar espera las aceptaciones de vacío o cambios masivos. El motor vuelve a comprobar las huellas. ProductSmoke exige botón deshabilitado antes de aceptar y rechazo también ante invocación directa. MirrorSmoke comprueba análisis cancelado y revisión persistida. |
| UI-18 | Retención reversible y purga explícita | RetentionSmoke comprueba fechas revisadas, objetos compartidos, cuarentena, deshacer sin origen, rechazo de gracia insuficiente y purga tras gracia sintética. Los motores contrastan referencias antes de modificar. |
| UI-19 | Retirada y actualización revisadas | ApplicationSmoke comprueba cambios de ajustes que invalidan la revisión, aceptación, retirada parcial reanudable y preservación de recuperación. InstallationProbe conserva archivos ajenos. |
| UI-20 | Teclado, foco, temas y tamaños | SurfaceSmoke, LayoutSmoke y UsabilitySmoke cubren Tab, focos de pasos, filas estables, nombres accesibles, cierre de ayuda, claro/oscuro y tamaño mínimo. El recorrido utiliza automatización WPF. |
| UI-21 | Versión exacta, guía y soporte | Ayuda muestra el sourceRevision de COMPILACION.json y el runtime del paquete. Guía y soporte se distribuyen y se representan localmente con enlaces HTTPS explícitos. La aceptación empaquetada exige los archivos y su contenido en las pestañas. |
| UI-22 | Diagnóstico local revisable | Campos permitidos de versión, entorno y recuentos agregados. Preparar muestra el texto antes de habilitar Copiar. UsabilitySmoke exige exclusión de nombres, IDs, rutas y archivos del fixture. La prueba evita modificar el portapapeles habitual. |
| UI-23 | Paquete reproducible y ubicación fácil | Publish-Portable desde dos árboles de Git limpios, comparación del manifiesto e inventario con Test-Portable, prueba de producto desde el paquete y copia local con runtime. Informe posterior separado. |
| UI-24 | Conservar motores y piloto | El diff del motor CopyProbe debe permanecer vacío desde la base de navegación. Fixture nuevo en temporal y configuración inyectada. Sin registrar tareas en el equipo habitual ni acceder al piloto C a D en pausa. |

## Reproducción

Usa el SDK fijado por global.json y una salida de artefactos nueva fuera del repositorio.

```powershell
dotnet build experiments/CopyLab/CopyLab.csproj -c Release --artifacts-path <salida-nueva> --disable-build-servers
# Desde la distribución final, ejecuta los recorridos con runtime incluido.
FolderTwin.exe --product-usability-smoke
FolderTwin.exe --product-smoke
CopyLab.exe --smoke
```

Los selectores, el registro y las operaciones de horario del arnés se inyectan para trabajar en espacios nuevos. Los trabajadores copian, verifican y restauran mediante los mismos comandos del producto. Los criterios comparan contenido y estados, además de observar etiquetas.

La primera corrección de acciones fijas expuso una pérdida de altura del panel de detalles en tamaño mínimo. Se compactó la cabecera, se trasladó Exportar a Editar trabajo, se agrupó Respaldos de alfa dentro de Aplicación y se reservaron 80 unidades para la lista compacta. El registro pendiente aparece cuando existe. Las comprobaciones exigen accesibilidad de detalles y acciones.

## Decisiones de ayuda y diagnóstico

La ayuda ampliada vive en una ventana con secciones expandibles. El tooltip es un acceso breve y la información de consecuencias permanece en el flujo de revisión. Se usa WPF sin nuevas dependencias. [Guía de tooltips de Windows](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/tooltips)

El diagnóstico se construye con una lista explícita de campos, independiente de los mensajes, rutas y nombres de trabajos. El usuario prepara, revisa y copia el texto. El envío al formulario de soporte queda como una acción propia.

## Validaciones externas y reanudación del piloto

El piloto de 7–14 días permanece en pausa por indicación del usuario. El entorno de pruebas C a D y sus resultados de la primera ronda se conservan fuera del repositorio. Este goal usa otros fixtures.

Siguen pendientes USB y HDD externos físicos, ciclos de Windows, otra cuenta o equipo, nube real y lector de pantalla y escalado reales. Para cada caso, PILOTO.md especifica acciones, resultado y evidencia. Antes de ensayar lector y DPI, registra versión de Windows, lector, monitor, escala y commit. Recorre asistente, acciones deshabilitadas, ayuda, incidencia y recuperación. Registra los nombres anunciados, el foco, controles recortados y la salida recuperada.

La aceptación del software se mantiene independiente de estas comprobaciones. Los arneses WPF son automatización programática y no acreditan una observación manual completa del escritorio.
