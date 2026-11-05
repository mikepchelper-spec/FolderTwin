# Notas del candidato de FolderTwin 1.0

Serie candidata de FolderTwin 1.0, sin declaración de v1 definitiva. La identidad exacta de cada paquete está en **COMPILACION.json**. El informe posterior de aceptación y el hash del ZIP se distribuyen por separado en [Releases](https://github.com/mikepchelper-spec/FolderTwin/releases); deben identificar el mismo commit y manifiesto. Estas notas describen funciones y cambios, sin anticipar el resultado de una compilación. Consulta [GUIA.md](GUIA.md) y [PILOTO.md](PILOTO.md).

## Cambios de esta entrega

- Las guías distribuidas se separan de los informes de aceptación de desarrollo. El ZIP identifica sus propias fuentes y runtime y no presenta un candidato anterior como actual.
- Se conserva el foco al abrir recuperación y al realizar consultas consecutivas con Enter. La revisión de interfaz cubre también el retorno entre paneles, los controles de revisión y los estados sin puntos.
- El historial conserva sus filas, selección y foco durante las consultas periódicas cuando su contenido no ha cambiado.

## Funciones disponibles

- Entrada **FolderTwin.exe** sin argumentos y asistente de origen, destino dedicado, modo, horario y revisión.
- Icono propio en el ejecutable, la ventana de producto y la bandeja, con recursos para varios tamaños.
- Copia acumulativa, versionado y espejo protegido sobre raíces locales NTFS, con identidades y bloqueo compartidos.
- Trabajadores independientes para respaldo, verificación, restauración y revisión del espejo, con progreso, controles e historial persistido.
- Navegación y búsqueda por fecha y carpeta, comparación de cambios y restauración en una salida nueva, incluso sin origen.
- Horarios diarios con sesión iniciada, reintentos acotados, bandeja opcional y conservación de incidencias por trabajo.
- Retención manual con revisión, cuarentena, deshacer y purga posterior a la gracia.
- Exportación e importación de configuración con revisión y activación expresa; acceso compatible a formatos anteriores mediante **Respaldos de alfa**.
- Revisión, instalación y actualización del ejecutor y retirada de horarios y versiones conservando respaldos y configuración.

## Distribución y actualización

El paquete incluye el runtime, hashes, licencias y documentación. El artefacto `FolderTwin-win-x64-<commit>` incluye el código fuente del commit. Dos compilaciones desde árboles limpios deben producir los mismos hashes antes de aceptar una entrega. CI conserva el artefacto 30 días. Guarda una copia local para todo el piloto.

Extrae una actualización en otra carpeta y conserva la anterior hasta verificar reapertura y restauración. **Aplicación** prepara una versión estable distinta y actualiza los horarios propios habilitados. No sobrescribe otra instalación para resolver un conflicto. La retirada comprueba los archivos, identidades y referencias directas de tareas consultables, mantiene un diario y permite reanudarla. No retira la versión que está abierta. [Procedimiento](INSTALACION.md).

Los almacenes nuevos y anteriores conservan lectores explícitos. No se permite usar un escritor antiguo para modificar un almacén después de adoptar eventos de registro que ese escritor no admite. No hay conversión automática de los espacios de alfa. [Compatibilidad](COMPATIBILIDAD.md).

## Pendientes antes de cerrar v1

- Recorrido completo de accesibilidad, lector activo y escalado real de Windows. El recorrido funcional de los tres modos en perfil de producto vacío pasó desde el artefacto descargado; no acredita otra cuenta o equipo.
- Piloto de 7–14 días y validaciones externas de USB NTFS físico, ciclos de Windows, otra cuenta o equipo y proveedor de nube real.

Estos puntos se mantienen abiertos hasta contar con evidencia. Los ensayos con VHDX, fallos inyectados y procesos terminados se registran por separado y no aprueban el comportamiento de un disco físico. El corte eléctrico físico queda excluido.

Los borradores y diarios conservados no se descartan dentro del almacén anterior: mantienen su espacio y restricciones de limpieza. El informe externo identifica qué pruebas pasaron desde cada paquete concreto.

## Límites del alcance

Windows 11 x64 y NTFS local. Retención manual. VSS, vigilancia continua, NAS, nube propia y sincronización bidireccional quedan fuera de 1.0. Mientras la matriz esté abierta, usa solo datos sintéticos y carpetas desechables; conserva aparte respaldos personales y FreeFileSync. No se promete pérdida cero ni recuperación frente a cualquier fallo físico.
