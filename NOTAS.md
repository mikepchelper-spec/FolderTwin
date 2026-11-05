# Notas de la vista previa de FolderTwin 1.0

Estado: desarrollo de v1. Este documento acompaña al paquete reproducible del commit que contiene estas notas; todavía no anuncia una versión final ni un candidato con aceptación completa. La [matriz de v1](V1.md) contiene los requisitos y evidencia. Consulta [GUIA.md](GUIA.md) para operar desde la interfaz y [PILOTO.md](PILOTO.md) para registrar el ensayo.

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

El paquete incluye el runtime, hashes, licencias y documentación. Las entregas de CI incluyen el código fuente del commit. Dos compilaciones desde árboles limpios deben producir los mismos hashes antes de aceptar una entrega. El nombre del artefacto todavía identifica la distribución de desarrollo como `FolderTwin-alpha-win-x64-…`.

Extrae una actualización en otra carpeta y conserva la anterior hasta verificar reapertura y restauración. **Aplicación** prepara una versión estable distinta y actualiza los horarios propios habilitados. No sobrescribe otra instalación para resolver un conflicto. La retirada comprueba los archivos, identidades y referencias directas de tareas consultables, mantiene un diario y permite reanudarla. No retira la versión que está abierta. [Procedimiento](INSTALACION.md).

Los almacenes nuevos y anteriores conservan lectores explícitos. No se permite usar un escritor antiguo para modificar un almacén después de adoptar eventos de registro que ese escritor no admite. No hay conversión automática de los espacios de alfa. [Compatibilidad](COMPATIBILIDAD.md).

## Pendientes antes de cerrar v1

- Aceptación desde el paquete de la resolución de planes mediante conservación del trabajo retirado y creación de otro destino. Sus doce casos WPF pasaron localmente. No se descartan borradores ni diarios dentro del almacén anterior; conserva su espacio y sus restricciones de limpieza.
- Aceptación integrada desde el artefacto final y recorrido interactivo completo desde una instalación limpia.
- Revisión final de identidad, textos, accesibilidad y escalado.
- Piloto de 7–14 días y validaciones externas de USB NTFS físico, ciclos de Windows, otra cuenta o equipo y proveedor de nube real.

Estos puntos se mantienen abiertos hasta contar con evidencia. Los ensayos con VHDX, fallos inyectados y procesos terminados se registran por separado y no aprueban el comportamiento de un disco físico. El corte eléctrico físico queda excluido.

## Límites del alcance

Windows 11 x64 y NTFS local. Retención manual. VSS, vigilancia continua, NAS, nube propia y sincronización bidireccional quedan fuera de 1.0. Mientras la matriz esté abierta, usa solo datos sintéticos y carpetas desechables; conserva aparte respaldos personales y FreeFileSync. No se promete pérdida cero ni recuperación frente a cualquier fallo físico.
