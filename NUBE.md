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
| Puntos confirmados de los tres modos, reintento tras respuesta perdida y restauración de dos fechas | Probado con Kopia real sobre almacenamiento de archivos desechable |
| Transporte S3 y rclone, cola persistente, credenciales y estados de Ruta | En desarrollo |
| B2, AWS, Filen y MEGA con cuentas reales | Pendiente. Verificar autorización, cuotas, recuperación en otro equipo y alcance de la protección contra borrado |
| Empaquetado Windows x64 y recorrido WPF | Pendiente |

El siguiente ensayo debe reutilizar estos puntos y pasar por un endpoint S3 local. La CLI de Kopia 0.23.1 marca su repositorio directo de rclone como sin mantenimiento. La segunda vía requiere comparar ese adaptador con una pasarela local acotada, documentar qué proceso conserva las credenciales y validar la recuperación antes de elegir.
