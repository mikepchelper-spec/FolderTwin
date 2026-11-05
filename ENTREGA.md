# Candidato de FolderTwin 1.0

Estado del 5 de noviembre de 2026: **candidato a lanzamiento 76b7eed, con aceptación de software aprobada y validaciones externas pendientes**. No se declara v1 final ni se ha iniciado el piloto. El candidato anterior 8bde43f quedó retenido tras reproducir un fallo de foco; se conserva como evidencia histórica.

## Artefacto elegido

| Dato | Valor |
|---|---|
| Código | `76b7eed6981381bb7242853f29f64768d4576b11` |
| Plataforma | Windows 11 x64, NTFS local; runtime .NET 10.0.14 incluido |
| CI de Windows | [42104199340 — aprobada](https://github.com/mikepchelper-spec/FolderTwin/actions/runs/42104199340) |
| Volumen virtual NTFS | [42105791375 — aprobada](https://github.com/mikepchelper-spec/FolderTwin/actions/runs/42105791375) |
| Artefacto de Actions | `FolderTwin-win-x64-76b7eed6981381bb7242853f29f64768d4576b11`, ID `13451048794` |
| ZIP | `FolderTwin-win-x64-76b7eed.zip`, 77.002.196 bytes |
| SHA-256 del ZIP descargado | `b287866c8eea33922d7747417ae66513ebe1d179586f272b0048c80753ae5190` |
| SHA-256 de SHA256SUMS.txt | `f4dd952099b35628c13d1791b903133316f2627f6f2e961c7bcd5fee518237f3` |
| Contenido comprobado | 506 archivos: cada hash y conjunto exacto; manifiesto separado |
| Reproducción | Dos árboles limpios en CI y otros dos locales; los cuatro manifiestos coinciden |

El paquete incluye ejecutables, runtime, código fuente del commit, licencia GPL, licencias de .NET, guía, instalación, compatibilidad, notas y procedimiento del piloto. No requiere instalar .NET aparte. El ZIP queda congelado. Las notas interiores reflejan el estado al compilar; este registro incorpora su aceptación posterior sin modificar el paquete. Conservar el ZIP y el manifiesto durante todo el piloto; los artefactos de Actions caducan a los 30 días.

El borrador **v1.0.0-rc.1**, ID `435666824`, se gestiona en [Releases](https://github.com/mikepchelper-spec/FolderTwin/releases). Sus adjuntos deben coincidir con esta tabla antes de distribuir el candidato. No se ha publicado una versión final. Se usa la lista de Releases porque GitHub cambió la URL temporal del borrador al editar sus notas.

## Aceptación del paquete

Windows CI aprobó copia, raíces, registro de trabajos, acumulativo, versiones, espejo, inventario, catálogos, reanudación, retención, configuración y trabajadores. Ejecutó WPF desde el paquete, compatibilidad, análisis del espejo, conservación de doce planes incompatibles y gestión de instalaciones. Los horarios nativos ejecutaron los tres modos y reintentaron el destino ausente tras cinco minutos, conservando la incidencia. La actualización cambió solo tareas propias habilitadas; la retirada eliminó dos runtimes reales y después restauró los tres modos. Una tarea ajena oculta bloqueó la retirada correspondiente y permaneció intacta.

El VHDX del mismo commit agotó capacidad después de comenzar la copia en cada modo, conservó el punto anterior y reanudó la misma generación al liberar espacio. Comprobó ausencia, sustitución por una letra reutilizada y retorno con otra letra; verificó y restauró los tres modos sin origen, conservando las incidencias. Es un volumen virtual desechable, no una prueba con USB físico.

La solicitud anterior `42068399899`, de 8bde43f, sigue figurando en cola sin trabajos en la API de consulta. Al intentar retirar esa solicitud obsoleta, GitHub respondió que ya estaba terminada. No se le asigna una conclusión verificable ni se usa como evidencia. La aceptación del candidato corregido corresponde a las dos ejecuciones aprobadas de la tabla; no se reinició un trabajador activo para obtenerla.

## Corrección de foco y escritorio

La revisión de teclado del candidato anterior reprodujo pérdida de foco al abrir recuperación y buscar con Enter. Una prueba con ventana visible falló antes de corregirlo. La restauración compartida del foco se hace después de habilitar y distribuir el panel: enfoca la fecha al entrar en recuperación y conserva el control anterior al consultar dentro del mismo panel. Rechaza controles de otra ventana, ocultos o deshabilitados.

La regresión pasó localmente y desde el paquete de CI. En dos sesiones aisladas del paquete corregido se observaron tema claro y tema oscuro del sistema, foco en fecha, Tab hacia búsqueda, consulta vacía con Enter, selección con Ctrl+A y otra consulta con coincidencias, sin clics entre consultas. Se revisaron selección de archivo, acceso a verificación y apertura de una carpeta mediante su botón con teclado. En tema claro se comprobó también el foco de la aceptación y del botón de instalación en Aplicación, sin ejecutar esa instalación desde la inspección. Las referencias de foco del controlador se contrastaron con indicadores visuales y entrada real.

## Recorrido normal EXT-06

Se abrió **FolderTwin.exe sin argumentos** del paquete reproducido localmente, idéntico por manifiesto y archivos al ZIP descargado de CI. Windows 11 x64, compilación 26100, cuenta existente con cero trabajos previos en el registro habitual. Se usaron selectores nativos, origen sintético propio y destinos dedicados nuevos; no argumentos de desarrollo ni espacio de laboratorio.

| Modo | Creación y respaldo | Verificación | Recuperación |
|---|---|---|---|
| Versionado | Asistente completo; punto confirmado | Completa | Archivo y carpeta vacía en salida nueva |
| Copia acumulativa | Asistente completo; copia confirmada | Completa | Archivo y carpeta vacía en otra salida nueva |
| Espejo protegido | Asistente, análisis visible y aprobación expresa antes de aplicar | Completa | Archivo y carpeta vacía en otra salida nueva |

Las tres salidas y el original coincidieron en SHA-256: `9a52172734bb9222e856377e058051db9bda3d596c580d45a1276da4f75e8c51`. Con los trabajadores inactivos se apartó temporalmente el mismo origen sintético. Versionado mostró **Esperando origen**, el aviso para reconectar y **Reintentar respaldo**, conservando la fecha del respaldo y verificación anteriores. Al devolver la carpeta, el reintento y su verificación terminaron bien. El historial conservó la incidencia y el éxito posterior.

Todas las acciones de producto se realizaron en la interfaz; la preparación sintética y los hashes se comprobaron por separado. No se crearon horarios reales en esta cuenta. Se cerró la aplicación y se archivaron solo los tres trabajos de prueba y su índice nuevo dentro del fixture. El perfil habitual volvió a sus entradas anteriores. No se tocaron datos personales. Otra cuenta o equipo conserva su validación independiente EXT-04A.

## Punto exacto de reanudación

No hay por ahora entorno externo de pruebas disponible. Por indicación del usuario, el piloto y las comprobaciones que lo necesitan permanecen pendientes, sin fechas ni resultados ficticios.

1. Conservar este ZIP, su manifiesto, commit y registro. Si se cambia código, identificar otro candidato y repetir las pruebas afectadas.
2. Cuando haya entorno, seguir [PILOTO.md](PILOTO.md) durante 7–14 días, registrando la fecha real de inicio y los resultados por modo. No contar tiempo de desarrollo como observación.
3. Completar EXT-02, EXT-03A/B/C, EXT-04A/B y EXT-05A/B/C: USB físico, sesión, reinicio, suspensión, otra cuenta/equipo, proveedor real de nube y aceptación completa de accesibilidad. La [matriz](V1.md) conserva las observaciones parciales de teclado y temas.
4. Resolver fallos y repetir sus pruebas. Declarar v1 final solo cuando toda la matriz obligatoria tenga evidencia.

No reiniciar ni cerrar la sesión del equipo de trabajo para sustituir el entorno ausente. El reinicio abrupto de VM se registra separado del corte de proceso. El corte eléctrico físico está excluido. VHDX, peers de UI Automation y tamaños de ventana WPF no acreditan USB físico, lector activo ni DPI real.
