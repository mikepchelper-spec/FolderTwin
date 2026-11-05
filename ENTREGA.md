# Candidato de FolderTwin 1.0

Estado del 5 de noviembre de 2026: **candidato a lanzamiento con validaciones pendientes**. El objetivo de v1 continúa abierto. Este registro se escribió después de comprobar el artefacto y complementa las notas y la matriz incluidas al compilarlo. El ZIP queda congelado; agregar evidencia no cambia sus bytes.

## Artefacto elegido

| Dato | Valor |
|---|---|
| Código | `8bde43f8b2cb8ba161938edb0b21c58502d73446` |
| Plataforma | Windows 11 x64, NTFS local; runtime .NET incluido |
| Paquete | `FolderTwin-win-x64-8bde43f8b2cb8ba161938edb0b21c58502d73446` |
| CI de Windows | [42068611369 — aprobada](https://github.com/mikepchelper-spec/FolderTwin/actions/runs/42068611369) |
| Artefacto de Actions | ID `13436900822`, 76.999.944 bytes |
| SHA-256 del ZIP descargado | `c5520de86811667b8d12c552f3b4f72c082e8d0977c74d9aa7d8560004bf8323` |
| SHA-256 de SHA256SUMS.txt | `3c337e9e757ebac4bf6eeccf32d778c57c8e828be1777c1032441e62abd83d02` |
| Contenido comprobado | 505 archivos, cada hash y conjunto exacto; manifiesto separado |
| Reproducción | Dos árboles limpios en CI y otros dos en Windows 11 local; los cuatro manifiestos coinciden |

El paquete incluye ejecutables, runtime 10.0.14, código fuente del commit, licencia GPL, licencias de .NET, guía, instalación, compatibilidad, notas y procedimiento del piloto. No requiere instalar .NET por separado. Las notas dentro del ZIP reflejan el estado anterior a esta aceptación; este registro y la [matriz actual](V1.md) describen sus resultados posteriores. Los artefactos de Actions caducan a los 30 días: conservar el ZIP y este registro durante todo el piloto.

## Aceptación del paquete

Windows CI aprobó los arneses de copia, raíces, registro de trabajos, acumulativo, versiones, espejo, inventario, catálogos, reanudación, retención, configuración y trabajadores. Ejecutó el recorrido WPF del paquete, compatibilidad de formatos anteriores, análisis del espejo, conservación de doce planes incompatibles, retención, importación y retirada de instalaciones.

Los horarios nativos ejecutaron los tres modos. El destino ausente se reintentó a los 301,1 segundos y conservó su incidencia. La actualización instaló otro ejecutor, cambió únicamente tareas propias habilitadas y verificó el resultado. La retirada eliminó dos runtimes reales y luego restauró los tres modos. Una tarea ajena oculta bloqueó la retirada correspondiente y permaneció intacta.

El [VHDX de 050da68](https://github.com/mikepchelper-spec/FolderTwin/actions/runs/42067012181) aprobó disco lleno después de comenzar la copia, ausencia, sustitución y retorno con otra letra para los tres modos, verificación y restauración sin origen. No hubo cambios del motor, su arnés o runtime entre ese commit y el candidato. La repetición exacta del candidato [42068399899](https://github.com/mikepchelper-spec/FolderTwin/actions/runs/42068399899) permanece en cola, sin trabajos asignados según la API. No hay resultado disponible ni causa de cancelación: no se canceló ni se reinició. Se mantiene como comprobación pendiente.

## Recorrido interactivo EXT-06

Se descargó el artefacto de Actions y se abrió **FolderTwin.exe sin argumentos** en Windows 11 x64, compilación 26100, cuenta existente. El registro habitual de FolderTwin tenía cero trabajos antes del ensayo. Se usó una carpeta temporal propia marcada, con un texto sintético y una carpeta vacía. No se utilizó el espacio de laboratorio ni argumentos de vista previa. La preparación y las comprobaciones independientes de hashes fueron externas a la interfaz; todas las acciones de producto se hicieron con sus controles y selectores nativos.

| Modo | Creación y respaldo | Verificación | Recuperación |
|---|---|---|---|
| Versionado | Asistente completo; punto confirmado | Completa | Archivo y carpeta vacía en salida nueva |
| Copia acumulativa | Asistente completo; copia confirmada | Completa | Archivo y carpeta vacía en otra salida nueva |
| Espejo protegido | Asistente; análisis visible; aprobación expresa antes de aplicar | Completa | Archivo y carpeta vacía en otra salida nueva |

Las tres salidas y el original coincidieron en SHA-256: `9a52172734bb9222e856377e058051db9bda3d596c580d45a1276da4f75e8c51`. Se observaron Enter, Tab y flechas del asistente y foco visible en tema oscuro.

Con los trabajadores inactivos se apartó temporalmente la misma carpeta de origen sintético. Versionado mostró **Esperando origen**, **Reintentar respaldo** y un aviso para reconectar. Conservó la fecha del respaldo y verificación anteriores. Al devolver la carpeta y reintentar, confirmó un punto nuevo y su verificación completa. El historial mantuvo la incidencia y el éxito posterior. No se crearon horarios reales en esta cuenta.

Se cerró la aplicación y se conservaron únicamente los tres trabajos sintéticos y su índice nuevo dentro de la carpeta del ensayo para revisión. El registro habitual volvió a sus entradas anteriores, preservando su archivo de bloqueo. No se tocaron datos personales. Este recorrido acredita un perfil de producto vacío en una cuenta existente; otra cuenta o equipo es EXT-04A.

## Punto exacto de reanudación

1. Consultar la ejecución **42068399899**, sin crear otra por falta temporal de salida. Incorporar su conclusión y registros. Si falla, preservar el fallo, corregir y seleccionar otro candidato con nueva aceptación.
2. Seguir [PILOTO.md](PILOTO.md) con este ZIP durante 7–14 días y registrar fecha real de inicio, modos, horarios, verificaciones, restauraciones e incidencias. No contar tiempo de desarrollo como observación.
3. Completar EXT-02, EXT-03A/B/C, EXT-04A/B y EXT-05A/B/C: USB físico, sesión, reinicio, suspensión, otra cuenta/equipo, proveedor real de nube y accesibilidad. Cada fila indica el entorno y la evidencia necesaria. No reiniciar ni cerrar la sesión del equipo de trabajo para obtener esos resultados.
4. Resolver los fallos encontrados y repetir sus pruebas. Declarar v1 final solo tras cerrar la matriz obligatoria. Una ausencia de evidencia mantiene el estado de candidato.

El reinicio abrupto en VM se registra separado del corte de proceso. El corte eléctrico físico está excluido. Pruebas VHDX, nombres accesibles por UI Automation y tamaños de ventana WPF no sustituyen USB físico, lector activo o DPI real.
