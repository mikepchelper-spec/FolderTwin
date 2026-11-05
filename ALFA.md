# FolderTwin · Alfa para Windows

Extrae todo el artefacto de Windows x64 en una carpeta nueva y abre `FolderTwin.exe`. La apertura habitual muestra los trabajos de producto. Para seguir las pruebas de laboratorio de esta guía, pulsa **Respaldos de alfa**. Conserva los archivos de la distribución juntos. El paquete incluye el runtime de .NET. Es una compilación experimental sin firma, para un espacio desechable. `CopyLab.exe` se conserva como entrada interna compatible; también abre los trabajos sin argumentos.

La [guía del piloto](PILOTO.md) incluye el procedimiento de observación y los criterios de aceptación y detención.

La vista previa de producto incluye **Respaldos de alfa** para abrir estas herramientas. El [procedimiento de compatibilidad](COMPATIBILIDAD.md) distingue espacios configurables, almacenes de fechas y demostraciones iniciales. La salida de una demostración anterior recuperada se conserva al cerrar y muestra su ruta al terminar.

1. Abre **Nuevo espacio de pruebas** y luego **Ver carpeta de pruebas**.
2. Crea tres subcarpetas directas y usa una como origen. Coloca unos archivos sintéticos y una subcarpeta vacía en ella. Deja vacíos el destino y la carpeta de restauración.
3. Selecciona origen y destino, pulsa **Analizar carpetas** y revisa el recuento, los bytes y el espacio disponible. Pulsa **Copiar y verificar**.
4. Cuando confirme el respaldo, usa **Verificar respaldo**. Para recuperar, pulsa **Restaurar y verificar** y elige la subcarpeta vacía. Comprueba que se abren los archivos restaurados y que aparecen las carpetas esperadas.
5. Cierra la ventana y vuelve a abrirla. En **Abrir espacio**, selecciona el espacio registrado durante la prueba. Usa **Abrir trabajo** y elige el destino. Comprueba que el historial y el estado del trabajo siguen disponibles.

Conserva el espacio si hay un problema y anota el mensaje, la operación y el commit de la compilación. Usa solo archivos de prueba y carpetas desechables. Los nuevos espacios quedan bajo `%LocalAppData%\FolderTwin-Lab\workspaces` y se conservan al cerrar. Las sesiones temporales anteriores también pueden reabrirse.

Para probar **Versiones**, crea en el mismo espacio otra subcarpeta vacía y selecciónala en **Crear almacén**. Elige el origen y crea un primer punto. Cambia un archivo sintético y elimina otro. Crea un segundo punto, selecciónalo y activa el filtro de cambios. Selecciona el archivo eliminado y restáuralo en una subcarpeta nueva y vacía. Comprueba que contiene los bytes que guardó la fecha anterior.

Para probar el ejecutor independiente, elige un origen y usa **Copiar en segundo plano**. Cierra la ventana y vuelve a abrir el espacio y el almacén. Pulsa **Actualizar** para ver el punto y el resultado del trabajo. Conserva el espacio mientras el trabajador esté ejecutándose. Un intento con incidencias aparece por separado de los puntos ya confirmados.

Para probar **Espejo protegido**, despliega esa sección de **Versiones**, pulsa **Preparar espejo** y elige el origen sintético. Revisa altas, reemplazos, retiradas, conflictos y espacio antes de aplicar. Si el origen está vacío o se alcanza el umbral configurado, la interfaz pide una revisión adicional. Tras confirmar, **Ver carpeta actual** abre el resultado y las fechas anteriores siguen en Versiones según su retención. Si se interrumpe, vuelve al mismo almacén y pulsa **Reanudar espejo**. Conserva el origen sin cambios mientras la preparación siga pendiente.

Para probar un horario desde el paquete portable, elige el origen o un trabajo guardado, escribe una hora próxima en formato HH:mm y pulsa **Programar**. El ejecutor se instala en una carpeta propia bajo LocalAppData. Mantén la sesión de Windows iniciada, cierra la interfaz y vuelve a abrir el almacén después de la hora elegida. Comprueba el resultado y verifica el punto. Usa **Quitar horario** al terminar el ensayo. Windows puede demorar la recuperación de un horario perdido. Si cambia la definición de la tarea o falla el arranque, la ventana pide revisión.

Si falta el origen o el destino, o el almacén está ocupado, el ejecutor programado registra el intento y espera cinco minutos antes de repetirlo. Hace hasta tres reintentos y vuelve a comprobar el horario y las identidades. Para actualizar un horario de una alfa anterior, vuelve a pulsar **Programar**.

Para probar la bandeja, pulsa **Minimizar a bandeja** desde el espacio de pruebas. Haz clic en el icono de FolderTwin para consultar los trabajos y usa **Respaldar ahora**. Abre la aplicación desde el panel o el menú del icono para revisar los puntos. El menú **Salir** cierra la interfaz. Los avisos de bandeja dependen de la configuración de notificaciones de Windows y sus incidencias también aparecen al abrir el panel.

En **Versiones**, despliega **Horario y bandeja**. Puedes activar **Mostrar estado al iniciar sesión** y pulsar **Aplicar inicio** para registrar el monitor del espacio con el usuario actual. Para retirarlo, desmarca la opción y vuelve a aplicar. La consulta inicial se hace después de abrir la sesión y usa la instalación estable del ejecutor. El recorrido de cerrar y abrir sesión en un equipo real forma parte de las comprobaciones del piloto.

`SHA256SUMS.txt` contiene el SHA-256 y la ruta relativa de cada archivo distribuido. El artefacto de GitHub incluye el código fuente del commit en `FolderTwin-source.tar` y las licencias de la distribución. El catálogo de la aplicación es independiente y verifica el contenido de los trabajos.

En almacenes nuevos, **Retención y cuarentena** permite revisar los días, semanas, meses y gracia. El calendario es UTC y se aplica por identidad de origen. **Revisar retención** no modifica los datos. Revisa las fechas que expirarán, marca la aceptación y pulsa **Mover a cuarentena**. Los objetos compartidos por puntos retenidos se conservan. La cuarentena no libera espacio. Si queda interrumpida, usa **Reanudar movimientos**. Conserva todos los archivos de su diario.

Selecciona una operación y usa **Deshacer cuarentena** para devolver los objetos y volver a retener esas fechas. Las fechas expiradas siguen mostrando su catálogo; deshaz la cuarentena antes de restaurarlas. Para purgar, espera la gracia completa desde que terminaron los movimientos, revisa de nuevo y acepta expresamente el borrado definitivo. Una purga iniciada no se puede deshacer. Un catálogo ausente, dañado o contradictorio, una copia pendiente o un objeto activo que no verifica detienen la limpieza. Una nueva copia correcta no autoriza por sí sola la purga. En esta fase no se activa limpieza automática.
