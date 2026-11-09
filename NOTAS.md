# Notas del candidato de FolderTwin 1.0

Serie candidata de FolderTwin 1.0 para ensayos sintéticos. **COMPILACION.json** identifica el paquete. El informe de aceptación y el hash del ZIP se entregan por separado y deben identificar el mismo commit y manifiesto. Consulta [la guía de uso](GUIA.md) y [el protocolo del piloto](PILOTO.md).

## Cambios de esta entrega

- Primer inicio con **Crear mi primer respaldo**, ejemplos de los tres modos y pasos para verificar y recuperar una muestra.
- **Automático al detectar cambios** inicia la primera copia al guardar un trabajo nuevo y agrupa los cambios siguientes. El monitor permanece activo con la ventana cerrada y puede arrancar al iniciar sesión.
- El espejo admite retiradas ordinarias recuperables cuando el usuario las autoriza por trabajo. La vigilancia reanuda el análisis al volver un destino registrado. Un archivo ocupado puede dejar un punto parcial con los datos legibles.
- **ⓘ Ayuda y versión** y F1 ofrecen explicaciones por control, requisitos de acciones deshabilitadas, guía local y soporte. El diagnóstico permite revisar y copiar un resumen mínimo de versión y estados.
- Las acciones habituales permanecen sobre los detalles desplazables. Una recuperación confirmada habilita **Abrir carpeta recuperada**.
- El espejo habilita Aplicar después de aceptar las revisiones adicionales que requiere el inventario.
- **Volver a trabajos** queda en la cabecera de Aplicación, Importar y Recuperar. La pantalla indica dónde estás y volver conserva el trabajo seleccionado.
- Candidata posterior a la retirada de RC1. Consulta la aceptación y el estado de publicación de esta compilación antes de ejecutarla; continúa siendo experimental y solo para datos sintéticos.
- El inicio de un trabajo y la revisión de un origen importado respetan los ajustes confirmados antes de adquirir su bloqueo de ejecución.
- La bandeja conserva por separado las incidencias del respaldo y de la verificación: verificar un punto anterior no resuelve un respaldo fallido.
- La lectura de diarios rechaza elementos nulos y secuencias agotadas de forma controlada, conservando los registros para revisión.
- La política de horarios detecta disparadores deshabilitados, restricciones de batería y ejecución manual deshabilitada.
- El acumulativo conserva el nombre ya existente en destino ante cambios solo de mayúsculas, incluidos sus padres, y sigue detectando ediciones externas. Los cambios archivo↔carpeta se detienen para conservar el contenido del destino.
- El índice local mantiene abiertos los archivos validados mientras SQLite los usa. Si falta su base y quedan diarios, la interfaz conserva esos archivos y navega desde los catálogos. Puede reconstruir el índice cuando falte la caché completa.
- La auditoría añade oráculos independientes de contenido, intercalaciones de concurrencia, recuperación y limpieza offline, y retirada con archivos ajenos incorporados tarde. Los informes posteriores detallan resultados y límites; estas notas no anticipan su aprobación.

## Funciones disponibles

- Entrada **FolderTwin.exe** sin argumentos y asistente de origen, destino dedicado, modo, disparadores y revisión.
- Icono propio en el ejecutable, la ventana de producto y la bandeja, con recursos para varios tamaños.
- Copia acumulativa, versionado y espejo protegido sobre raíces locales NTFS, con identidades y bloqueo compartidos.
- Trabajadores independientes para respaldo, verificación, restauración y revisión del espejo, con progreso, controles e historial persistido.
- Navegación y búsqueda por fecha y carpeta, comparación de cambios y restauración en una salida nueva, incluso sin origen.
- Vigilancia y horarios diarios con sesión iniciada, reintentos acotados, bandeja opcional y conservación de incidencias por trabajo.
- Retención manual con revisión, cuarentena, deshacer y purga posterior a la gracia.
- Exportación e importación de configuración con revisión y activación expresa; acceso compatible a formatos anteriores mediante **Respaldos de alfa**.
- Revisión, instalación y actualización del ejecutor y retirada de horarios y versiones conservando respaldos y configuración.

## Distribución y actualización

El paquete incluye el runtime, hashes, licencias y documentación. El artefacto `FolderTwin-win-x64-<commit>` incluye el código fuente del commit. Dos compilaciones desde árboles limpios deben producir los mismos hashes antes de aceptar una entrega. CI conserva el artefacto 30 días. Guarda una copia local para todo el piloto.

Extrae una actualización en otra carpeta y conserva la anterior hasta verificar reapertura y restauración. **Aplicación** prepara el ejecutor de la nueva versión, traspasa los horarios propios habilitados y actualiza el monitor registrado. La retirada comprueba archivos, identidades y referencias directas de tareas consultables y mantiene un diario para reanudarla. Abre otra copia portable si necesitas retirar una versión que está abierta. [Procedimiento](INSTALACION.md).

Los almacenes nuevos y anteriores conservan lectores explícitos. No se permite usar un escritor antiguo para modificar un almacén después de adoptar eventos de registro que ese escritor no admite. No hay conversión automática de los espacios de alfa. [Compatibilidad](COMPATIBILIDAD.md).

## Pendientes antes de cerrar v1

- Recorrido completo de accesibilidad, lector activo y escalado real de Windows. El recorrido funcional de los tres modos en perfil de producto vacío pasó desde el artefacto descargado; no acredita otra cuenta o equipo.
- Piloto de 7–14 días y validaciones externas de USB NTFS físico, ciclos de Windows, otra cuenta o equipo y proveedor de nube real.

Estos puntos se mantienen abiertos hasta contar con evidencia. Los ensayos con VHDX, fallos inyectados y procesos terminados se registran por separado y no aprueban el comportamiento de un disco físico. El corte eléctrico físico queda excluido.

Los borradores y diarios conservados no se descartan dentro del almacén anterior: mantienen su espacio y restricciones de limpieza. El informe externo identifica qué pruebas pasaron desde cada paquete concreto.

## Límites del alcance

Windows 11 x64 y NTFS local. Retención manual. VSS, NAS, nube propia y sincronización bidireccional quedan fuera. La vigilancia requiere una sesión iniciada y el equipo despierto. Los respaldos pueden agrupar varios guardados en un mismo punto. Usa solo datos sintéticos y carpetas desechables. Conserva aparte los respaldos personales y FreeFileSync.
