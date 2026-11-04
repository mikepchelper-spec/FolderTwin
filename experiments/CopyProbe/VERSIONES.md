# Almacén experimental de generaciones

El almacén ocupa una subcarpeta dedicada de un espacio desechable registrado. Se inicializa en una carpeta vacía y usa el marcador de destino y un registro de identidad propio. Todas las operaciones mantienen un bloqueo exclusivo del almacén. Los datos de otras carpetas y los respaldos de copia plana quedan fuera de este formato.

| Ubicación | Contenido |
|---|---|
| `.foldertwin-version-store` | 48 bytes. ASCII `FTVERS01`, identidad de directorio de 24 bytes y GUID del marcador en el orden de `Guid.ToByteArray()`. |
| `objects/sha256/ab/<sha256>` | Bytes del flujo principal completo. El nombre es el SHA-256 hexadecimal en minúscula y `ab` son sus dos primeros caracteres. |
| `manifests/<id>.jsonl` y `manifests_backup/<id>.jsonl` | Réplicas del catálogo de la generación. |
| `commits/<id>.commit` | Confirmación de 112 bytes con el formato de [CATALOG.md](CATALOG.md). |

El identificador de generación es UTC en formato `yyyyMMddTHHmmssfffffffZ`, seguido de un guion y 32 dígitos hexadecimales de un GUID. El catálogo reutiliza los registros JSONL, versiones y límites del lector de lotes. La cabecera lleva el identificador de generación en vez de `initial` y la identidad y marcador del almacén. El hash de cada registro de archivo determina la única ruta de objeto admitida. Los nombres originales y los metadatos de restauración pertenecen al catálogo. Dos archivos con los mismos bytes pueden compartir un objeto y restaurar con fechas o atributo Oculto diferentes.

Se relee cada objeto reutilizado para verificar tamaño y SHA-256. Los objetos nuevos se escriben por temporal y registro de recuperación, con vaciado y verificación antes de renombrar sin reemplazo. Los objetos que ya existen con contenido incorrecto se conservan para revisión. Después de comprobar el inventario y los objetos, se publican las dos réplicas y por último la confirmación. La lectura abre únicamente generaciones confirmadas. Si una réplica falta o está dañada se puede usar la otra con aviso. Dos réplicas válidas pero diferentes bloquean la lectura.

El reintento experimental recibe el mismo identificador y vuelve a analizar el origen. Si ya hay una réplica de esa generación, sus bytes deben coincidir para completar la publicación. Los borradores incompletos y los objetos sobrantes se conservan. La lista de este ensayo enumera las confirmaciones presentes. El registro durable de generaciones esperadas para retención queda pendiente.

La restauración verifica los objetos y copia cada entrada a una carpeta vacía. Usa los metadatos de cada ruta del catálogo. Puede ejecutarse con el origen ausente y sin un índice. El arnés `--versions` cubre la restauración de un archivo borrado en el origen, la pérdida de una réplica, el conflicto de réplicas válidas, la publicación interrumpida y el daño de un objeto compartido. La interfaz de versiones, SQLite, las mediciones de distribución y la retención son hitos posteriores.
