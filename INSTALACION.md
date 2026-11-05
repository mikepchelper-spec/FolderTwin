# Instalación y ciclo de la aplicación

La distribución actual es portable y sigue siendo una vista previa de v1. No necesita instalar .NET por separado. Esta guía no sustituye los criterios pendientes de V1-12.

Extrae todo el paquete en una carpeta nueva y abre `FolderTwin.exe`. Conserva juntos ejecutables, bibliotecas, runtime, licencias, guías y `SHA256SUMS.txt`. No mezcles dos distribuciones copiando archivos encima de una carpeta anterior. La aplicación abre los trabajos de producto sin argumentos. **Respaldos de alfa** mantiene el acceso a las herramientas anteriores.

## Ejecutor y actualización

Al guardar o reparar un horario, FolderTwin comprueba el manifiesto del paquete y copia su ejecutor a `%LocalAppData%\FolderTwin\app\<hash-del-paquete>`. Las versiones anteriores permanecen separadas. El marcador de instalación solo se completa después de comprobar los archivos. El manifiesto comprueba corrupción accidental; no es una firma del editor.

Una instalación interrumpida puede completarse desde el mismo paquete cuando cada archivo parcial coincide exactamente con su prefijo. Un archivo diferente, datos desconocidos o un manifiesto contradictorio detienen la instalación y se conservan para revisión. No se reemplaza una versión anterior para hacer que una actualización termine. Ejecutar la preparación desde la propia versión instalada vuelve a comprobarla sin copiar sobre sí misma.

Para actualizar, cierra la interfaz anterior y espera a que terminen sus trabajadores. Extrae el paquete nuevo en otra carpeta, ábrelo y entra en **Aplicación**. Revisa los trabajos, acepta la actualización y pulsa **Instalar o actualizar ejecutor**. Las tareas propias de los trabajos habilitados pasan al ejecutor del paquete nuevo; los deshabilitados permanecen sin horario. Los respaldos y la configuración están fuera de la carpeta de la aplicación. Mantén la distribución anterior hasta verificar la nueva y recuperar una muestra. Si una versión nueva ya adoptó un evento de formato nuevo, una compilación antigua puede rechazarlo; consulta [Compatibilidad](COMPATIBILIDAD.md).

La revisión se vincula a la configuración observada. Cambiar un trabajo obliga a revisar de nuevo. La gestión adquiere los bloqueos de todos los trabajos y rechaza una copia, restauración o reintento activo. Comprueba primero las tareas nativas: una tarea ajena o en ejecución detiene la gestión antes de instalar o retirar horarios. Una interrupción a mitad de actualizar tareas puede dejar una parte en la versión nueva y otra en la anterior; no se anuncia una actualización completa. Vuelve a **Aplicación** para revisar y repetir. Ambas instalaciones se conservan.

## Retirada actual

En **Aplicación**, revisa los trabajos, acepta **Deshabilitar todos los trabajos y retirar sus horarios propios** y pulsa **Retirar todos los horarios**. Quita cada tarea propia antes de guardar su estado deshabilitado. Conserva configuración, historial y datos; los trabajos siguen disponibles para recuperar. Una retirada parcial muestra la incidencia, sin anunciar que terminó. Vuelve a revisar y repite para completar los trabajos restantes.

Cierra la interfaz y deja terminar cualquier trabajador o reintento. Puedes retirar la carpeta portable cuando ya no esté abierta. La limpieza controlada de versiones instaladas sigue en implementación; no borres carpetas de respaldos ni la configuración para desinstalar. También puedes deshabilitar o retirar un trabajo individual desde **Editar trabajo y horario**.

## Evidencia disponible y pendiente

El arnés de instalación usa paquetes de bytes sintéticos que no se ejecutan: interrumpe archivos y registros, completa los prefijos exactos, rechaza corrupción y archivos ajenos y crea otra versión sin modificar la primera. El recorrido WPF de producto ejecuta ese arnés. La aceptación nativa de horarios comprueba además el paquete real y su instalación estable en un runner desechable.

El recorrido WPF local añade revisión y aceptación, gestión desde la ventana principal, rechazo de configuración obsoleta, exclusión de trabajadores y reintentos, repetición de una retirada parcial y verificación y restauración de los tres modos después de deshabilitar. El arnés nativo de CI prepara un segundo paquete, comprueba que los horarios habilitados apunten a él, ejecuta su trabajador, retira tareas y restaura los tres modos. Incluye una tarea sintética con propietario distinto que debe conservarse sin instalar ni deshabilitar trabajos. Pasó desde el paquete reproducible de `45d32e7` ([CI](https://github.com/mikepchelper-spec/FolderTwin/actions/runs/42038982994)).

Faltan el flujo público y la aceptación nativa de limpieza de versiones y la aceptación del artefacto final de v1. Estos puntos permanecen abiertos en [V1.md](V1.md).

## Motor de retirada de versiones en prueba

El motor ya puede revisar y retirar una carpeta de versión identificada por el hash del paquete. Todavía no está conectado a una acción de borrado en la interfaz. Su revisión conserva las identidades de la raíz de instalaciones y de la versión y el manifiesto original. Antes de comenzar abre los archivos mediante handles, comprueba sus hashes y rechaza archivos en uso, datos desconocidos, enlaces o contenido distinto. No usa una eliminación recursiva por una ruta de texto.

El diario de esquema 1 vive en `app/.removals/<hash>/plan` y se publica antes del primer borrado. Conserva el manifiesto suficiente para revisar los archivos que sigan presentes tras una interrupción. Los temporales del plan y del recibo solo se completan si son prefijos exactos. Primero se retiran los archivos del paquete y sus carpetas vacías; manifiesto y marcador quedan para el final. El recibo de terminación se publica después de retirar la raíz. Reanudar comprueba las identidades y todos los archivos restantes; no autoriza borrar archivos que aparezcan después y no pertenezcan al manifiesto.

La reinstalación del mismo paquete conserva el diario completo en un directorio de historial antes de crear la versión otra vez. El cambio de nombre no reemplaza otro directorio; todos los handles de los documentos del diario se cierran antes de archivarlo. Se siguen las restricciones de [FILE_RENAME_INFORMATION](https://learn.microsoft.com/windows-hardware/drivers/ddi/ntifs/ns-ntifs-_file_rename_information). Una revisión de la raíz anterior no puede retirar la reinstalación, aunque tenga el mismo hash. La versión desde la que se está ejecutando la aplicación se rechaza expresamente: deberá retirarse desde otra copia portable.

El arnés sintético pasó los siete límites: plan parcial, plan publicado, archivos retirados, carpetas retiradas, raíz retirada, recibo parcial y terminación. Reabre y repite cada retirada, conserva un archivo de respaldo externo, reinstala y rechaza una revisión de la identidad anterior. También conserva por completo una instalación ocupada, con datos ajenos o con un archivo cambiado. Estas pruebas no sustituyen el ensayo del paquete real y el flujo público de desinstalación.
