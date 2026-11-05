# Instalación y ciclo de la aplicación

La distribución actual es portable y sigue siendo una vista previa de v1. No necesita instalar .NET por separado. Esta guía no sustituye los criterios pendientes de V1-12.

Extrae todo el paquete en una carpeta nueva y abre `FolderTwin.exe`. Conserva juntos ejecutables, bibliotecas, runtime, licencias, guías y `SHA256SUMS.txt`. No mezcles dos distribuciones copiando archivos encima de una carpeta anterior. La aplicación abre los trabajos de producto sin argumentos. **Respaldos de alfa** mantiene el acceso a las herramientas anteriores.

## Ejecutor y actualización

Al guardar o reparar un horario, FolderTwin comprueba el manifiesto del paquete y copia su ejecutor a `%LocalAppData%\FolderTwin\app\<hash-del-paquete>`. Las versiones anteriores permanecen separadas. El marcador de instalación solo se completa después de comprobar los archivos. El manifiesto comprueba corrupción accidental; no es una firma del editor.

Una instalación interrumpida puede completarse desde el mismo paquete cuando cada archivo parcial coincide exactamente con su prefijo. Un archivo diferente, datos desconocidos o un manifiesto contradictorio detienen la instalación y se conservan para revisión. No se reemplaza una versión anterior para hacer que una actualización termine. Ejecutar la preparación desde la propia versión instalada vuelve a comprobarla sin copiar sobre sí misma.

Para actualizar ahora, cierra la interfaz anterior y espera a que terminen sus trabajadores. Extrae el paquete nuevo en otra carpeta, ábrelo y repara los horarios de los trabajos habilitados. Las tareas propias pasan al ejecutor del paquete nuevo; los respaldos y la configuración permanecen fuera de la carpeta de la aplicación. Mantén la distribución anterior hasta verificar la nueva y recuperar una muestra. Si una versión nueva ya adoptó un evento de formato nuevo, una compilación antigua puede rechazarlo; consulta [Compatibilidad](COMPATIBILIDAD.md).

## Retirada actual

Antes de retirar la aplicación, deshabilita o retira cada trabajo desde **Editar trabajo y horario** y guarda. Esto retira su tarea propia del Programador conservando los datos. Cierra la interfaz y deja terminar cualquier trabajador o reintento. Puedes retirar la carpeta portable cuando ya no esté abierta. La limpieza de versiones instaladas y el flujo unificado de retirada siguen en implementación; no borres carpetas de respaldos ni la configuración para desinstalar.

## Evidencia disponible y pendiente

El arnés de instalación usa paquetes de bytes sintéticos que no se ejecutan: interrumpe archivos y registros, completa los prefijos exactos, rechaza corrupción y archivos ajenos y crea otra versión sin modificar la primera. El recorrido WPF de producto ejecuta ese arnés. La aceptación nativa de horarios comprueba además el paquete real y su instalación estable en un runner desechable.

Faltan el flujo de gestión desde la interfaz, la retirada y limpieza controlada de versiones, su aceptación nativa de actualización y la aceptación del artefacto final. Estos puntos permanecen abiertos en [V1.md](V1.md).
