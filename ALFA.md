# FolderTwin · Alfa para Windows

Extrae todo el artefacto de Windows x64 en una carpeta nueva y abre `CopyLab.exe`. Conserva los archivos de la distribución juntos. El paquete incluye el runtime de .NET. Es una compilación experimental sin firma, para un espacio desechable.

1. Abre **Nuevo espacio temporal** y luego **Ver carpeta temporal**.
2. Crea tres subcarpetas directas y usa una como origen. Coloca unos archivos sintéticos y una subcarpeta vacía en ella. Deja vacíos el destino y la carpeta de restauración.
3. Selecciona origen y destino, pulsa **Analizar carpetas** y revisa el recuento, los bytes y el espacio disponible. Pulsa **Copiar y verificar**.
4. Cuando confirme el respaldo, usa **Verificar respaldo**. Para recuperar, pulsa **Restaurar y verificar** y elige la subcarpeta vacía. Comprueba que se abren los archivos restaurados y que aparecen las carpetas esperadas.
5. Cierra la ventana y vuelve a abrirla. En **Abrir espacio**, selecciona la carpeta temporal indicada durante la prueba. Usa **Abrir trabajo** y elige el destino. Comprueba que el historial y el estado del trabajo siguen disponibles.

Conserva la carpeta temporal si hay un problema y anota el mensaje, la operación y el commit de la compilación. Usa solo archivos de prueba y carpetas desechables. Las sesiones quedan en el espacio temporal al cerrar.

`SHA256SUMS.txt` contiene el SHA-256 y la ruta relativa de cada archivo distribuido. El artefacto de GitHub incluye el código fuente del commit en `FolderTwin-source.tar` y las licencias de la distribución. El catálogo de la aplicación es independiente y verifica el contenido de los trabajos.
