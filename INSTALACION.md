# Instalación y ciclo de la aplicación

La distribución portable es una candidata de software de v1 e incluye .NET. El informe de aceptación se entrega por separado e identifica el paquete probado.

Extrae todo el paquete en una carpeta nueva y abre `FolderTwin.exe`. Conserva juntos ejecutables, bibliotecas, runtime, licencias, guías y `SHA256SUMS.txt`. No mezcles dos distribuciones copiando archivos encima de una carpeta anterior. La aplicación abre los trabajos de producto sin argumentos. **Respaldos de alfa** mantiene el acceso a las herramientas anteriores.

## Ejecutor y actualización

Al preparar la vigilancia o un horario, FolderTwin comprueba el manifiesto del paquete y copia su ejecutor a `%LocalAppData%\FolderTwin\app\<hash-del-paquete>`. Las versiones anteriores permanecen separadas. El marcador de instalación solo se completa después de comprobar los archivos. El manifiesto comprueba corrupción accidental. La publicación indica si el paquete lleva firma del editor.

Una instalación interrumpida puede completarse desde el mismo paquete cuando cada archivo parcial coincide exactamente con su prefijo. Un archivo diferente, datos desconocidos o un manifiesto contradictorio detienen la instalación y se conservan para revisión. No se reemplaza una versión anterior para hacer que una actualización termine. Ejecutar la preparación desde la propia versión instalada vuelve a comprobarla sin copiar sobre sí misma.

Para actualizar, cierra la interfaz anterior y espera a que terminen sus trabajadores. Extrae el paquete nuevo en otra carpeta, ábrelo y entra en **Aplicación**. Revisa los trabajos, acepta la actualización y pulsa **Instalar o actualizar ejecutor**. Los horarios propios y la tarea activa del monitor pasan al ejecutor nuevo. El monitor conserva su opción de iniciar con la sesión. Los respaldos y la configuración están fuera de la carpeta de la aplicación. Conserva la distribución anterior hasta verificar la nueva y recuperar una muestra. Una compilación anterior puede rechazar eventos de formato nuevo. Consulta [Compatibilidad](COMPATIBILIDAD.md).

La revisión se vincula a la configuración observada. Cambiar un trabajo obliga a revisar de nuevo. La gestión adquiere los bloqueos de todos los trabajos y rechaza una copia, restauración o reintento activo. Comprueba primero las tareas nativas: una tarea ajena o en ejecución detiene la gestión antes de instalar o retirar horarios. Una interrupción a mitad de actualizar tareas puede dejar una parte en la versión nueva y otra en la anterior; no se anuncia una actualización completa. Vuelve a **Aplicación** para revisar y repetir. Ambas instalaciones se conservan.

## Retirada actual

Para detener solo la vigilancia, abre **Aplicación → Detener vigilancia**. Los horarios diarios siguen activos y un intento ya iniciado puede terminar. Si quieres retirar toda la automatización, revisa los trabajos en **Aplicación** y confirma la retirada. FolderTwin quita el monitor y cada tarea propia antes de guardar los trabajos como deshabilitados. La configuración, el historial y los respaldos quedan disponibles para recuperar. Si aparece una incidencia, vuelve a revisar y repite la retirada para los trabajos restantes.

En la misma sección pulsa **Revisar versiones instaladas**, elige la versión y comprueba su ubicación. Acepta **Eliminar únicamente la versión instalada seleccionada** y pulsa **Eliminar versión revisada**. Si una retirada quedó interrumpida, la lista la identifica y la acción continúa desde su diario. Una tarea que siga apuntando directamente a esa versión impide retirarla. La consulta incluye tareas ocultas y subcarpetas consultables del Programador, siguiendo [GetTasks](https://learn.microsoft.com/en-us/windows/win32/api/taskschd/nf-taskschd-itaskfolder-gettasks) y [GetFolders](https://learn.microsoft.com/en-us/windows/desktop/api/taskschd/nf-taskschd-itaskfolder-getfolders). Las definiciones ajenas solo se consultan; no se retiran ni modifican. Un error de consulta detiene el borrado.

La versión abierta no se puede elegir para borrar. Abre otra copia portable para retirarla. Al terminar, cierra FolderTwin y retira la carpeta portable si ya no la necesitas. La configuración y los respaldos permanecen disponibles al extraer de nuevo el paquete. También puedes deshabilitar o retirar un trabajo individual desde **Editar trabajo y horario**.

## Resultados por paquete

El informe de aceptación se prepara después de probar el paquete. Comprueba su commit y manifiesto contra `COMPILACION.json` y `SHA256SUMS.txt` antes de usar sus resultados. [Historial público de entregas](https://github.com/mikepchelper-spec/FolderTwin/releases).

## Motor de retirada de versiones en prueba

La acción pública usa el motor de revisión y retirada de una carpeta de versión identificada por el hash del paquete. Su revisión conserva las identidades de la raíz de instalaciones y de la versión y el manifiesto original. Antes de comenzar abre los archivos mediante handles, comprueba sus hashes y rechaza archivos en uso, datos desconocidos, enlaces o contenido distinto. No usa una eliminación recursiva por una ruta de texto.

El diario de esquema 1 vive en `app/.removals/<hash>/plan` y se publica antes del primer borrado. Conserva el manifiesto suficiente para revisar los archivos que sigan presentes tras una interrupción. Los temporales del plan y del recibo solo se completan si son prefijos exactos. Primero se retiran los archivos del paquete y sus carpetas vacías; manifiesto y marcador quedan para el final. El recibo de terminación se publica después de retirar la raíz. Reanudar comprueba las identidades y todos los archivos restantes; no autoriza borrar archivos que aparezcan después y no pertenezcan al manifiesto.

La reinstalación del mismo paquete conserva el diario completo en un directorio de historial antes de crear la versión otra vez. El cambio de nombre no reemplaza otro directorio; todos los handles de los documentos del diario se cierran antes de archivarlo. Se siguen las restricciones de [FILE_RENAME_INFORMATION](https://learn.microsoft.com/windows-hardware/drivers/ddi/ntifs/ns-ntifs-_file_rename_information). Una revisión de la raíz anterior no puede retirar la reinstalación, aunque tenga el mismo hash. La versión desde la que se está ejecutando la aplicación se rechaza expresamente: deberá retirarse desde otra copia portable.

El arnés sintético interrumpe la retirada en siete fases, desde el plan parcial hasta el recibo final. Reabre cada retirada, conserva un archivo de respaldo externo, reinstala y rechaza una revisión de la identidad anterior. También conserva una instalación ocupada, con datos ajenos o con un archivo cambiado. Los resultados de cada paquete y las validaciones externas se registran por separado.
