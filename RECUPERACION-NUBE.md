# Recuperar desde una tercera copia

Esta guía acompaña al paquete experimental de Windows 11 x64. Usa datos sintéticos y una carpeta NTFS desechable. Identifica la compilación en **Ayuda y versión** y en `COMPILACION.json`. Comprueba que el informe de aceptación separado identifica esa misma compilación.

## Datos para conservar fuera del equipo

Guarda la contraseña de cifrado del repositorio en tu gestor de contraseñas o medio de emergencia. Guarda también el proveedor, servidor y región S3 si corresponden, el bucket y la carpeta exacta del repositorio. Para Filen o MEGA guarda la carpeta dedicada y la forma de obtener acceso a la cuenta. Conserva el paquete FolderTwin capaz de leer tus fechas y su hash. El acceso guardado por Windows está protegido para su usuario. Para recuperar desde un perfil vacío tendrás que introducir credenciales vigentes.

## Restaurar desde FolderTwin

1. Extrae todo el ZIP y abre **FolderTwin.exe**. En **Más acciones → Copia en nube**, elige el proveedor. Introduce la ubicación, las credenciales de lectura y la contraseña del repositorio.
2. Pulsa **Consultar fechas de nube**. Comprueba fecha y trabajo. Para una fecha completa, pulsa **Recuperar fecha completa**.
3. Para recuperar una parte, pulsa **Elegir archivos o carpetas**. Abre una carpeta con **Entrar en carpeta** o busca por nombre. Selecciona una entrada y pulsa **Recuperar selección**. Las fechas antiguas ofrecen recuperación completa.
4. Elige dónde crear la carpeta nueva. Consulta **Intentos de recuperación** para ver la salida y las fases. Durante la descarga la barra indica actividad. Al comprobar y entregar los archivos muestra recuentos. Puedes cerrar la ventana y regresar al mismo intento sin volver a conectar.
5. Espera **Recuperación completada y verificada** antes de usar los archivos. Abre la carpeta que figura en el intento y comprueba una muestra contra lo esperado. Las fechas nuevas conservan `Zone.Identifier` en NTFS. Para cancelar, selecciona el intento y pulsa **Cancelar recuperación**.

Si falta espacio, el acceso caducó o el proceso terminó, el intento conserva su estado y la ruta elegida. La salida parcial se conserva si se creó. Comprueba la conexión y vuelve a elegir una fecha para iniciar otro intento en una carpeta nueva. **Cerrar intento interrumpido** retira la credencial protegida de ese intento. Los trabajos locales siguen disponibles mientras hay una recuperación de nube.

## Extracción de emergencia con Kopia

Usa un equipo y almacenamiento desechables para practicar. Trabaja con una conexión de **solo lectura**. En S3, conecta Kopia a la ubicación conservada. Introduce la contraseña en el aviso de Kopia. Evita incluir contraseñas o claves en comandos, archivos de diagnóstico o capturas. Para Filen o MEGA conserva una copia completa de la carpeta cifrada `foldertwin` obtenida durante una pausa de escrituras. El formato de blobs de S3 requiere servir esa copia a Kopia por S3. Usa `rclone serve s3` en loopback con `--read-only`, autenticación efímera y una carpeta que contenga el bucket `foldertwin`. Conecta Kopia al puerto que indique rclone, región `us-east-1` y bucket `foldertwin`. Cierra el servidor local al terminar. [Recuperar con Kopia](https://kopia.io/docs/reference/command-line/common/snapshot-restore/) · [Servidor S3 de rclone](https://rclone.org/commands/rclone_serve_s3/)

Lista fechas con `kopia snapshot list --all --show-identical --json`. Las antiguas usan `tag:ft-job` y `tag:ft-generation`. Las nuevas usan `tag:ft-v2-job` y `tag:ft-v2-generation`. Selecciona el `rootEntry.obj` del trabajo y fecha correctos y restaura con `kopia snapshot restore <rootEntry.obj> <carpeta-nueva>`. Revisa los errores e inventario de Kopia.

El formato nuevo entrega un sobre con `data/` y `.foldertwin-remote-catalog.jsonl`. Conserva ambos. La etiqueta `tag:ft-catalog` contiene el SHA-256 del catálogo y `tag:ft-catalog-bytes` su tamaño. Puedes cotejarlo con `Get-FileHash -LiteralPath <catálogo> -Algorithm SHA256`. Kopia 0.23.1 recupera el contenido de `data/`. El lector de FolderTwin verifica el catálogo y vuelve a aplicar la marca de procedencia de Windows antes de entregar los archivos. Evita ejecutar contenidos extraídos directamente del sobre. [Formato y ensayo sintético](experiments/CopyProbe/ZONE-IDENTIFIER.md)

La validación con AWS, B2, Filen y MEGA reales, otra cuenta o equipo y controles de borrado del proveedor está pendiente. El repositorio cifrado y su copia local pueden verse afectados por quien tenga permisos de escritura o eliminación en la cuenta remota. Conserva las claves de lectura y mantenimiento separadas cuando el proveedor lo permita.
