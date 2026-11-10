# Tercera copia de FolderTwin

La copia remota usa un repositorio cifrado de Kopia. El respaldo local conserva su propio historial y sus controles. El exportador abre el punto confirmado bajo el mismo bloqueo que usa la retención, comprueba su registro y restaura el contenido a una carpeta temporal privada. La transferencia comienza después de soltar el bloqueo local. Las carpetas de trabajo y los resultados del laboratorio usan datos sintéticos.

## Evidencia inicial

En Windows x64, Kopia 0.23.1 creó seis instantáneas en un repositorio de archivos desechable. Se publicaron dos por modo y se repitió cada envío tras simular la pérdida de su confirmación local. Kopia devolvió la instantánea existente. Una conexión separada de solo lectura, con caché nueva y con origen y respaldo local apartados, restauró ambas fechas de los tres modos. La prueba conservó la carpeta vacía y el archivo `.kopiaignore`. La segunda fecha conservó el archivo retirado en acumulativo y respetó el borrado en versionado y espejo protegido.

| Punto | SHA-256 del archivo editado |
| --- | --- |
| Primero | `9a6ba53c523a9b4234bd3613b23a1454491da3608dade3ca4981c4663b3074d3` |
| Segundo | `dee183796854d9964fd54bf3f91cdc80343f75ab277bcbc93ce8d75c4d5188e8` |

El ensayo detectó que una política vacía vuelve a heredar `.kopiaignore`. El repositorio de prueba desactiva esa regla y el mantenimiento automático. El adaptador rechaza puntos con archivos excluidos, fallos de lectura o un inventario distinto del exportado. El ensayo comprueba el contenido después de restaurar. La prueba de permisos y el ciclo de Object Lock requieren un proveedor compatible.

Ejecuta `dotnet run --project experiments/CopyProbe -- --cloud-point <ruta-absoluta-a-kopia.exe>` en Windows. Para reproducir esta prueba, usa el ZIP Windows x64 de [Kopia 0.23.1](https://github.com/kopia/kopia/releases/tag/v0.23.1), con SHA-256 `f0369d9657dbe47a1b8b6ff4e308c5958a4813b405d4e32ae9d81b3f2b3d8251`. El ejecutable extraído tiene SHA-256 `96c0946314b50dcc7c5ae9d3ec7e3763e742fcebf7d5cbebdaac3ceee5fb3bd7`. [Licencia de Kopia](https://github.com/kopia/kopia/blob/v0.23.1/LICENSE)

## Matriz de aceptación

| Alcance | Estado |
| --- | --- |
| Puntos confirmados de los tres modos, reintento tras respuesta perdida y restauración de dos fechas | Probado con Kopia sobre almacenamiento de archivos y sobre un endpoint S3 desechable |
| Cola persistente y despacho S3 | El despacho de producto reabre las credenciales protegidas y envía un punto descubierto en el historial local. El monitor y los controles de Ruta compilan. Queda su recorrido de aceptación WPF |
| Filen y MEGA mediante rclone | Los perfiles comparten la pasarela S3 local. El laboratorio sustituyó las cuentas por almacenamiento desechable y restauró dos fechas por perfil |
| B2, AWS, Filen y MEGA con cuentas reales | Pendiente. Verificar autorización, cuotas, recuperación en otro equipo y alcance de la protección contra borrado |
| Empaquetado Windows x64 y recorrido WPF | La compilación intermedia `b47c470` incluye Kopia y abre Ruta en un perfil aislado. La reproducción y la aceptación de nube desde el ZIP final siguen pendientes |

El ensayo S3 usa [rclone serve s3](https://rclone.org/commands/rclone_serve_s3/) en loopback, con una clave sintética temporal. Repite los seis puntos y sus restauraciones. Comprueba el cliente S3 y la recuperación, con el servidor de laboratorio como destino. Las pruebas de Object Lock y las políticas de B2 o AWS se ejecutarán por separado.

Ejecuta `experiments/AutomationAcceptance/Test-CloudPoint.ps1 -Kopia <kopia.exe> -Rclone <rclone.exe>`. Usa [rclone 1.76.0](https://github.com/rclone/rclone/releases/tag/v1.76.0), con SHA-256 del ZIP Windows x64 `8b4fd2737ade4e70c057ef4a49deb7cffe94b0fb9d7587c189d149823f21a1dc` y del ejecutable `acd80683e23151e93bbe0d6ffa74d26e542ec510332f630beecb9944f8d1a623`. [Licencia de rclone](https://github.com/rclone/rclone/blob/v1.76.0/COPYING)

La CLI de Kopia 0.23.1 marca su repositorio directo de rclone como sin mantenimiento. FolderTwin usa su adaptador S3 con una pasarela rclone por sesión en `127.0.0.1`, en un puerto asignado por Windows. El puente exige una clave efímera de S3, usa transferencia directa sin caché de escritura y se cierra al cerrar su proceso propietario. El repositorio se guarda bajo una carpeta dedicada del proveedor. En modo recuperación el puente y Kopia abren en solo lectura. Las credenciales de Filen y MEGA se guardan bajo DPAPI y rclone las recibe en el entorno de su proceso. [Pasarela S3 de rclone](https://rclone.org/commands/rclone_serve_s3/) · [Configuración Filen](https://rclone.org/filen/) · [Configuración MEGA](https://rclone.org/mega/)

En Windows, `CopyProbe --cloud-rclone <kopia.exe> <rclone.exe>` repite creación, envío y recuperación con una raíz local sintética. Las dos fechas de ambos perfiles devolvieron SHA-256 `2820cef141a8156abba5f7ec9ee8916fb0a400ad04dcc6cac255248f3e2063a8` y `ec920d1c6bd929506dc016dff768e67859ebe648ca350ceaafb8449fdde58624`. La prueba valida el flujo común, la configuración de perfiles y el reinicio de la pasarela. Autenticación, doble factor, nombres duplicados y cuotas de cuentas reales conservan su validación externa. Kopia verifica el contenido cifrado durante la recuperación. Ruta muestra ambos proveedores como experimentales.

## Cola de envíos

El registro local usa la identidad confirmada de Kopia, el identificador del trabajo y el identificador de la generación para derivar una etiqueta estable por envío. La cola verifica la identidad del repositorio al abrir la conexión. Las transiciones quedan en un diario separado del historial de copias locales. El bloqueo del trabajador evita dos envíos simultáneos del mismo punto y el bloqueo del diario permite grabar una pausa o cancelación mientras el trabajador está activo. Los reintentos transitorios esperan 30, 60 y 120 segundos. Tras el cuarto intento, la cola conserva la incidencia para revisión.

El ensayo vuelve a abrir el registro para verificar pausas, cancelaciones y reintentos. En Versionado termina un proceso real después de publicar el segundo punto y antes de escribir el resultado local. Comprueba que un segundo trabajador quede excluido durante el envío, aparta las dos carpetas locales y reconcilia la misma instantánea sin crear otra. También conecta deliberadamente el trabajador a otro repositorio y comprueba que rechace el envío. Los demás puntos simulan la pérdida de la confirmación y cotejan el historial desde una conexión de lectura con caché nueva.

Para S3, Kopia recibe las credenciales en el entorno de su propio proceso. El ensayo usa campos explícitamente vacíos al crear y conectar el repositorio para impedir que la CLI copie las credenciales del entorno al JSON. Comprueba que la configuración generada no contenga la clave, el secreto S3 ni la contraseña del repositorio. Una conexión nueva rechaza el secreto inválido y luego abre el mismo repositorio con el secreto correcto. El registro de producto usa DPAPI del usuario y liga la credencial al trabajo, repositorio y revisión. La aceptación de laboratorio cierra y reabre ese registro, descubre un respaldo confirmado y lo envía. Después recupera el punto con una conexión nueva de lectura, sin el registro original ni los discos locales. [Implementación S3 de Kopia 0.23.1](https://github.com/kopia/kopia/blob/v0.23.1/repo/blob/s3/s3_storage.go#L323)

El paquete de desarrollo admite `scripts/Publish-Portable.ps1 -OutputDirectory <ruta-nueva> -CloudKopiaArchive <ZIP-de-Kopia> -CloudRcloneArchive <ZIP-de-rclone> -CloudRcloneLicense <COPYING-oficial>`. Su manifiesto incluye ambos ejecutables y sus licencias. `scripts/New-PortableZip.ps1` crea un ZIP con orden y marcas de tiempo fijos. En Ruta, **Copia en nube** abre la configuración y permite consultar fechas con una conexión de solo lectura, incluso sin trabajos locales.

Para recuperar con Kopia independiente, conecta el repositorio en modo de lectura, ejecuta `kopia snapshot list --all --show-identical --json`, identifica el trabajo por `tag:ft-job` y la fecha por `tag:ft-generation`, y restaura el identificador `rootEntry.obj` en una carpeta nueva. Para Filen o MEGA puedes descargar la carpeta cifrada del repositorio a un espacio desechable con rclone y abrir `foldertwin` como repositorio de archivos en Kopia. Guarda fuera del equipo la ubicación del repositorio y la contraseña. [Recuperación en Kopia](https://kopia.io/docs/reference/command-line/common/snapshot-restore/)

La extracción intermedia de `b47c470533e190641cf9b56a533c2c2eb6fabfaf` pasó `Test-Portable.ps1` con 502 archivos y el manifiesto SHA-256 `5a3462493abc47351b1b63a5fb138c00325fa44dd494746ed101d9e83add21b6`. El ensayo `Test-IsolatedStartup.ps1` abrió `FolderTwin.exe` sin argumentos, verificó Ruta y cerró con el registro aislado vacío.
