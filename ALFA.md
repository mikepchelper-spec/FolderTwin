# FolderTwin · Alfa para Windows

Extrae todo el artefacto de Windows x64 en una carpeta nueva y abre `CopyLab.exe`. Conserva los archivos de la distribución juntos. El paquete incluye el runtime de .NET. Es una compilación experimental sin firma, para un espacio desechable.

1. Abre **Nuevo espacio de pruebas** y luego **Ver carpeta de pruebas**.
2. Crea tres subcarpetas directas y usa una como origen. Coloca unos archivos sintéticos y una subcarpeta vacía en ella. Deja vacíos el destino y la carpeta de restauración.
3. Selecciona origen y destino, pulsa **Analizar carpetas** y revisa el recuento, los bytes y el espacio disponible. Pulsa **Copiar y verificar**.
4. Cuando confirme el respaldo, usa **Verificar respaldo**. Para recuperar, pulsa **Restaurar y verificar** y elige la subcarpeta vacía. Comprueba que se abren los archivos restaurados y que aparecen las carpetas esperadas.
5. Cierra la ventana y vuelve a abrirla. En **Abrir espacio**, selecciona la carpeta temporal indicada durante la prueba. Usa **Abrir trabajo** y elige el destino. Comprueba que el historial y el estado del trabajo siguen disponibles.

Conserva el espacio si hay un problema y anota el mensaje, la operación y el commit de la compilación. Usa solo archivos de prueba y carpetas desechables. Los nuevos espacios quedan bajo `%LocalAppData%\FolderTwin-Lab\workspaces` y se conservan al cerrar. Las sesiones temporales anteriores también pueden reabrirse.

Para probar **Versiones**, crea en el mismo espacio otra subcarpeta vacía y selecciónala en **Crear almacén**. Elige el origen y crea un primer punto. Cambia un archivo sintético y elimina otro. Crea un segundo punto, selecciónalo y activa el filtro de cambios. Selecciona el archivo eliminado y restáuralo en una subcarpeta nueva y vacía. Comprueba que contiene los bytes que guardó la fecha anterior.

Para probar el ejecutor independiente, elige un origen y usa **Copiar en segundo plano**. Cierra la ventana y vuelve a abrir el espacio y el almacén. Pulsa **Actualizar** para ver el punto y el resultado del trabajo. Conserva el espacio mientras el trabajador esté ejecutándose. Un intento con incidencias aparece por separado de los puntos ya confirmados.

Para probar un horario desde el paquete portable, elige el origen o un trabajo guardado, escribe una hora próxima en formato HH:mm y pulsa **Programar**. El ejecutor se instala en una carpeta propia bajo LocalAppData. Mantén la sesión de Windows iniciada, cierra la interfaz y vuelve a abrir el almacén después de la hora elegida. Comprueba el resultado y verifica el punto. Usa **Quitar horario** al terminar el ensayo. Windows puede demorar la recuperación de un horario perdido. Si cambia la definición de la tarea o falla el arranque, la ventana pide revisión.

Para probar la bandeja, pulsa **Minimizar a bandeja** desde el espacio de pruebas. Haz clic en el icono de FolderTwin para consultar los trabajos y usa **Respaldar ahora**. Abre la aplicación desde el panel o el menú del icono para revisar los puntos. El menú **Salir** cierra la interfaz. Los avisos de bandeja dependen de la configuración de notificaciones de Windows y sus incidencias también aparecen al abrir el panel.

`SHA256SUMS.txt` contiene el SHA-256 y la ruta relativa de cada archivo distribuido. El artefacto de GitHub incluye el código fuente del commit en `FolderTwin-source.tar` y las licencias de la distribución. El catálogo de la aplicación es independiente y verifica el contenido de los trabajos.
